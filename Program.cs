using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CoffeeLoyalty.Branding;
using CoffeeLoyalty.Config;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Host allowlist. Deployment concern rather than client branding, so it comes from the
// environment: ALLOWED_HOSTS="qos.myloyalty.com,loyalty.mycafe.com". Unset means allow all,
// which is only safe because the container is not directly reachable from the internet.
var allowedHosts = Environment.GetEnvironmentVariable("ALLOWED_HOSTS");
if (!string.IsNullOrWhiteSpace(allowedHosts))
{
    var hosts = allowedHosts.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries
                                                     | StringSplitOptions.TrimEntries);
    builder.Services.PostConfigure<HostFilteringOptions>(o => o.AllowedHosts = hosts);
}

var app = builder.Build();

// ---------------------------------------------------------------------------
// Reverse proxy / tunnel awareness
//
// cloudflared (or a reverse proxy) terminates TLS and forwards to this container over
// plain HTTP. Without this, req.Scheme is "http" and every absolute URL built from it is
// wrong — including the programme logo URI handed to Google Wallet, which Google fetches
// from its own servers.
//
// X-Forwarded-Host is deliberately NOT trusted: cloudflared and Traefik both pass the
// original Host through natively, and honouring the header would let a caller rewrite the
// hostname in generated URLs.
// ---------------------------------------------------------------------------
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
    ForwardLimit = null   // the proxy may be several hops away inside the Docker network
};
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Clear();

// Defaults cover loopback and the RFC1918 ranges Docker networks live on. Override with
// TRUSTED_PROXY_NETWORKS="10.0.0.0/8,172.18.0.0/16", or "none" to ignore forwarded headers.
var trustedProxies = Environment.GetEnvironmentVariable("TRUSTED_PROXY_NETWORKS")
                     ?? "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";
if (!trustedProxies.Equals("none", StringComparison.OrdinalIgnoreCase))
{
    foreach (var cidr in trustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                 | StringSplitOptions.TrimEntries))
    {
        if (System.Net.IPNetwork.TryParse(cidr, out var parsed))
            forwardedOptions.KnownNetworks.Add(new IPNetwork(parsed.BaseAddress, parsed.PrefixLength));
        else
            app.Logger.LogWarning("Ignoring unparseable TRUSTED_PROXY_NETWORKS entry: {cidr}", cidr);
    }
    app.UseForwardedHeaders(forwardedOptions);
}

// Note: no UseHttpsRedirection. TLS is terminated at the tunnel/proxy and this container
// only ever speaks HTTP, so redirecting here would loop.

// ---------------------------------------------------------------------------
// Database
// ---------------------------------------------------------------------------
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "loyalty.db");
var connString = $"Data Source={dbPath}";

SqliteConnection Open()
{
    var c = new SqliteConnection(connString);
    c.Open();
    return c;
}

void InitDb()
{
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS customers (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            token TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            email TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS transactions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            customer_id INTEGER NOT NULL REFERENCES customers(id),
            type TEXT NOT NULL CHECK (type IN ('earn','redeem','adjust')),
            amount_pence INTEGER NOT NULL DEFAULT 0,
            points INTEGER NOT NULL,
            description TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS rewards (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            points_cost INTEGER NOT NULL,
            active INTEGER NOT NULL DEFAULT 1
        );
        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        INSERT OR IGNORE INTO settings (key, value) VALUES
            ('staff_pin', '1234'),
            ('admin_pin', '9999'),
            ('google_wallet_issuer_id', ''),
            ('google_wallet_service_account_json', ''),
            ('public_base_url', '');
        """;
    cmd.ExecuteNonQuery();

    // Schema migrations — safe to re-run; SQLite throws if column already exists, which we ignore
    foreach (var ddl in new[]
    {
        "ALTER TABLE rewards ADD COLUMN type TEXT NOT NULL DEFAULT 'points'",
        "ALTER TABLE rewards ADD COLUMN item_name TEXT",
        "ALTER TABLE customers ADD COLUMN marketing_ok INTEGER NOT NULL DEFAULT 0"
    })
    {
        try { var m = c.CreateCommand(); m.CommandText = ddl; m.ExecuteNonQuery(); } catch { }
    }

    // Seed a starter reward so the system works out of the box
    var count = c.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM rewards";
    if (Convert.ToInt64(count.ExecuteScalar()) == 0)
    {
        var seed = c.CreateCommand();
        seed.CommandText = "INSERT INTO rewards (name, points_cost) VALUES ('Free regular coffee', 100)";
        seed.ExecuteNonQuery();
    }
}
InitDb();

// ---------------------------------------------------------------------------
// Client configuration and branding
//
// Defaults (code) -> config/client.json (provisioning seed) -> DB settings (admin UI, wins).
// Uploaded assets live on the data volume so they survive image upgrades; the read-only
// config mount only ever holds the seed.
// ---------------------------------------------------------------------------
var configDir = Environment.GetEnvironmentVariable("CLIENT_CONFIG_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "config");
var webRoot = app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
var clientConfig = new ConfigStore(configDir, Open);
var assets = new AssetResolver(Path.Combine(dataDir, "assets"), Path.Combine(configDir, "assets"), webRoot);
var pages = new PageTemplate(clientConfig, assets);

// Renders a wwwroot page through the template layer instead of serving it as a static file.
// Endpoints take precedence over UseStaticFiles, so these win for the same paths.
IResult Page(string relativePath)
{
    var full = Path.Combine(webRoot, relativePath);
    return File.Exists(full)
        ? Results.Content(pages.Render(full), "text/html; charset=utf-8")
        : Results.NotFound();
}

IResult Templated(string relativePath, string contentType)
{
    var full = Path.Combine(webRoot, relativePath);
    return File.Exists(full)
        ? Results.Content(pages.Render(full), contentType)
        : Results.NotFound();
}

void SaveSettings(SqliteConnection c, IEnumerable<KeyValuePair<string, string>> values)
{
    foreach (var (key, value) in values)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
    clientConfig.Invalidate();
}

// Builds a Wallet client scoped to this establishment's class/object namespace. Sharing a
// platform issuer without distinct suffixes makes clients overwrite each other's pass design.
GoogleWalletService WalletService(SqliteConnection c)
{
    var w = clientConfig.Current.Wallet;
    return new GoogleWalletService(
        GetSetting(c, "google_wallet_issuer_id"),
        GetSetting(c, "google_wallet_service_account_json"),
        w.ClassSuffix, w.ObjectPrefix);
}

// Pass branding, falling back to the theme's primary when no explicit pass colour is set.
async Task EnsureWalletClass(GoogleWalletService svc, string issuerName, string programName, string baseUrl)
{
    var cfg = clientConfig.Current;
    var background = string.IsNullOrWhiteSpace(cfg.Wallet.BackgroundColor)
        ? cfg.Theme.BrandPrimary
        : cfg.Wallet.BackgroundColor;
    await svc.EnsureClassExists(issuerName, programName, baseUrl, background, assets.Url("walletLogo"), cfg.Locale.Language);
}

// The establishment's own name and programme label. These used to be read from the
// `shop_name` / `wallet_program_name` settings rows, which are no longer seeded — config
// maps those rows when an existing install has them and falls back to client.json.
string ShopName() => clientConfig.Current.Identity.ShopName;
string WalletProgramName()
{
    var configured = clientConfig.Current.Wallet.ProgramName;
    return string.IsNullOrWhiteSpace(configured) ? $"{ShopName()} Loyalty" : configured;
}

// Absolute origin for URLs that leave the app — currently the Google Wallet programme
// logo, which Google fetches from its own servers.
//
// A configured identity.publicBaseUrl always wins. Without one we fall back to the
// requesting host and remember it, so a single-hostname install keeps working with no
// configuration; but a client reachable on two hostnames should set it explicitly or the
// stored value flaps between them.
string ResolvedBaseUrl(SqliteConnection c, HttpRequest? req)
{
    var configured = clientConfig.Current.Identity.PublicBaseUrl.Trim().TrimEnd('/');
    if (configured.Length > 0) return configured;
    if (req is not null) return $"{req.Scheme}://{req.Host}";
    return GetSetting(c, "public_base_url");
}

// Only remembers the request host when no canonical URL is configured.
void RememberBaseUrl(SqliteConnection c, HttpRequest req)
{
    if (clientConfig.Current.Identity.PublicBaseUrl.Trim().Length > 0) return;
    var cmd = c.CreateCommand();
    cmd.CommandText = "INSERT INTO settings (key,value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
    cmd.Parameters.AddWithValue("$k", "public_base_url");
    cmd.Parameters.AddWithValue("$v", $"{req.Scheme}://{req.Host}");
    cmd.ExecuteNonQuery();
}

/// Formats minor currency units using the client's symbol and decimal places.
string Money(long minor)
{
    var loc = clientConfig.Current.Locale;
    var value = minor / Math.Pow(10, loc.CurrencyMinorDigits);
    return loc.CurrencySymbol + value.ToString("N" + loc.CurrencyMinorDigits,
        System.Globalization.CultureInfo.InvariantCulture);
}

string GetSetting(SqliteConnection c, string key)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
    cmd.Parameters.AddWithValue("$k", key);
    return (string?)cmd.ExecuteScalar() ?? "";
}

long ScalarLong(SqliteConnection c, string sql)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    var val = cmd.ExecuteScalar();
    return val is null or DBNull ? 0 : Convert.ToInt64(val);
}

string? ScalarText(SqliteConnection c, string sql)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar() as string;
}

long PointsBalance(SqliteConnection c, long customerId)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(SUM(points),0) FROM transactions WHERE customer_id = $id";
    cmd.Parameters.AddWithValue("$id", customerId);
    return Convert.ToInt64(cmd.ExecuteScalar());
}

long TotalSpendPence(SqliteConnection c, long customerId)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(SUM(amount_pence),0) FROM transactions WHERE customer_id = $id AND type = 'earn'";
    cmd.Parameters.AddWithValue("$id", customerId);
    return Convert.ToInt64(cmd.ExecuteScalar());
}

(long id, string name, string? email, string created)? FindCustomerByToken(SqliteConnection c, string token)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT id, name, email, created_at FROM customers WHERE token = $t";
    cmd.Parameters.AddWithValue("$t", token);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return null;
    return (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3));
}

List<object> RecentActivity(SqliteConnection c, long customerId, int limit = 15)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = """
        SELECT type, amount_pence, points, description, created_at
        FROM transactions WHERE customer_id = $id
        ORDER BY id DESC LIMIT $lim
        """;
    cmd.Parameters.AddWithValue("$id", customerId);
    cmd.Parameters.AddWithValue("$lim", limit);
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new
        {
            type = r.GetString(0),
            amountPence = r.GetInt64(1),
            points = r.GetInt64(2),
            description = r.IsDBNull(3) ? null : r.GetString(3),
            createdAt = r.GetString(4)
        });
    return list;
}

List<object> ActiveRewards(SqliteConnection c)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT id, name, points_cost, type, item_name FROM rewards WHERE active = 1 ORDER BY points_cost";
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new
        {
            id = r.GetInt64(0),
            name = r.GetString(1),
            pointsCost = r.GetInt64(2),
            type = r.GetString(3),
            itemName = r.IsDBNull(4) ? null : r.GetString(4)
        });
    return list;
}

// ---------------------------------------------------------------------------
// Realtime push (SSE hub): token -> set of subscriber channels
// ---------------------------------------------------------------------------
var subscribers = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<string>>>();

void Notify(string token)
{
    if (!subscribers.TryGetValue(token, out var subs)) return;
    using var c = Open();
    var cust = FindCustomerByToken(c, token);
    if (cust is null) return;
    var rate = (long)clientConfig.Current.Programme.PointsPerUnit;
    var payload = JsonSerializer.Serialize(new
    {
        points = PointsBalance(c, cust.Value.id),
        pointsPerPound = rate,
        totalSpendPence = TotalSpendPence(c, cust.Value.id),
        activity = RecentActivity(c, cust.Value.id),
        rewards = ActiveRewards(c)
    });
    foreach (var ch in subs.Values)
        ch.Writer.TryWrite(payload);
}

// Builds trophy stamp grid text modules for Google Wallet passes
List<(string header, string body)> StampModulesForPass(SqliteConnection c, long points)
{
    var modules = new List<(string header, string body)>();
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT name, points_cost, item_name FROM rewards WHERE active = 1 AND type = 'stamp' ORDER BY points_cost";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var prog = clientConfig.Current.Programme;
        var slots = r.GetInt64(1);
        var itemName = r.IsDBNull(2) ? prog.DefaultItemNoun : r.GetString(2);
        var filled = Math.Min(points, slots);
        var grid = string.Concat(Enumerable.Repeat(prog.StampEmojiFilled, (int)filled))
                 + string.Concat(Enumerable.Repeat(prog.StampEmojiEmpty, (int)(slots - filled)));
        var status = filled >= slots ? "Ready to redeem!" : $"{filled} of {slots} {itemName}s";
        modules.Add((r.GetString(0), $"{grid}\n{status}"));
    }
    return modules;
}

// Google Wallet: fire-and-forget update after any transaction
async Task NotifyWallet(string token)
{
    try
    {
        using var c = Open();
        var issuerId = GetSetting(c, "google_wallet_issuer_id");
        var saJson = GetSetting(c, "google_wallet_service_account_json");
        if (string.IsNullOrEmpty(issuerId) || string.IsNullOrEmpty(saJson)) return;

        var cust = FindCustomerByToken(c, token);
        if (cust is null) return;
        var shopName = ShopName();
        var points = PointsBalance(c, cust.Value.id);
        var stampModules = StampModulesForPass(c, points);

        var svc = WalletService(c);
        var publicBase = ResolvedBaseUrl(c, null);
        var programName = WalletProgramName();
        await EnsureWalletClass(svc, shopName, programName, publicBase);
        await svc.UpsertObject(token, cust.Value.name, points, programName, stampModules);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Google Wallet update failed: {msg}", ex.Message);
    }
}

// ---------------------------------------------------------------------------
// PIN guard helpers (simple shared-PIN auth for shop & admin)
// ---------------------------------------------------------------------------
bool PinOk(HttpRequest req, string settingKey)
{
    using var c = Open();
    var expected = GetSetting(c, settingKey);
    return req.Headers.TryGetValue("X-Pin", out var pin) && pin == expected;
}
IResult Unauthorized() => Results.Json(new { error = "Invalid PIN" }, statusCode: 401);

// ---------------------------------------------------------------------------
// Admin: Google Wallet diagnostic endpoint
// ---------------------------------------------------------------------------
// Force-updates the LoyaltyClass design on Google's servers (removes heroImage, applies current branding)
app.MapPost("/api/admin/wallet-update-class", async (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var issuerId = GetSetting(c, "google_wallet_issuer_id");
    var saJson = GetSetting(c, "google_wallet_service_account_json");
    if (string.IsNullOrEmpty(issuerId) || string.IsNullOrEmpty(saJson))
        return Results.Json(new { error = "Credentials not configured" }, statusCode: 400);

    var baseUrl = ResolvedBaseUrl(c, req);
    RememberBaseUrl(c, req);   // only when no canonical URL is configured

    try
    {
        var shopName = ShopName();
        var programName = WalletProgramName();
        var svc = WalletService(c);
        await EnsureWalletClass(svc, shopName, programName, baseUrl);
        return Results.Ok(new { ok = true, baseUrl });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

// What the app believes about how it is being reached. The first thing to check when a
// client's DNS, tunnel or proxy is newly wired up, or when Wallet passes show a broken logo.
app.MapGet("/api/admin/diagnostics", (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var configured = clientConfig.Current.Identity.PublicBaseUrl.Trim().TrimEnd('/');
    return Results.Ok(new
    {
        requestScheme = req.Scheme,                       // "https" only if a trusted proxy said so
        requestHost = req.Host.Value,
        forwardedProto = req.Headers["X-Forwarded-Proto"].ToString(),
        remoteIp = req.HttpContext.Connection.RemoteIpAddress?.ToString(),
        configuredBaseUrl = configured,
        rememberedBaseUrl = GetSetting(c, "public_base_url"),
        effectiveBaseUrl = ResolvedBaseUrl(c, req),       // what Google Wallet is given
        allowedHosts = Environment.GetEnvironmentVariable("ALLOWED_HOSTS") ?? "(any)",
        trustedProxyNetworks = Environment.GetEnvironmentVariable("TRUSTED_PROXY_NETWORKS") ?? "(defaults)"
    });
});

app.MapGet("/api/admin/wallet-test", async (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var issuerId = GetSetting(c, "google_wallet_issuer_id");
    var saJson = GetSetting(c, "google_wallet_service_account_json");
    if (string.IsNullOrEmpty(issuerId) || string.IsNullOrEmpty(saJson))
        return Results.Json(new { error = "Credentials not configured" }, statusCode: 400);

    try
    {
        var svc = WalletService(c);
        var baseUrl = ResolvedBaseUrl(c, req);
        var result = await svc.TestConnection(ShopName(), baseUrl);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message, type = ex.GetType().Name }, statusCode: 500);
    }
});

// Returns the raw loyalty object from Google's servers — use to confirm textModulesData is stored
app.MapGet("/api/admin/wallet-object/{customerToken}", async (HttpRequest req, string customerToken) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var issuerId = GetSetting(c, "google_wallet_issuer_id");
    var saJson = GetSetting(c, "google_wallet_service_account_json");
    if (string.IsNullOrEmpty(issuerId) || string.IsNullOrEmpty(saJson))
        return Results.Json(new { error = "Google Wallet not configured" }, statusCode: 400);
    try
    {
        var svc = WalletService(c);
        var (status, body) = await svc.GetObjectRaw(customerToken);
        return Results.Content(body, "application/json", statusCode: status);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

// ---------------------------------------------------------------------------
// Public / customer endpoints
// ---------------------------------------------------------------------------
app.MapPost("/api/join", (JsonElement body) =>
{
    var name = body.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
    var email = body.TryGetProperty("email", out var e) ? e.GetString()?.Trim() : null;
    var marketingOk = body.TryGetProperty("marketingOk", out var mo) && mo.GetBoolean();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "Name is required" });

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "INSERT INTO customers (token, name, email, marketing_ok) VALUES ($t, $n, $e, $m)";
    cmd.Parameters.AddWithValue("$t", token);
    cmd.Parameters.AddWithValue("$n", name);
    cmd.Parameters.AddWithValue("$e", (object?)email ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$m", marketingOk ? 1 : 0);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { token, cardUrl = $"/card/{token}" });
});

// Public lightweight check — tells the card page whether to show wallet buttons
app.MapGet("/api/admin/wallet-status", () =>
{
    using var c = Open();
    return Results.Ok(new
    {
        googleConfigured = !string.IsNullOrEmpty(GetSetting(c, "google_wallet_issuer_id"))
                        && !string.IsNullOrEmpty(GetSetting(c, "google_wallet_service_account_json"))
    });
});

app.MapGet("/api/customer/{token}", (string token) =>
{
    using var c = Open();
    var cust = FindCustomerByToken(c, token);
    if (cust is null) return Results.NotFound(new { error = "Unknown card" });
    var cfg = clientConfig.Current;
    var rate = (long)cfg.Programme.PointsPerUnit;
    return Results.Ok(new
    {
        name = cust.Value.name,
        shopName = cfg.Identity.ShopName,
        pointsPerPound = rate,
        stampIcon = cfg.Programme.StampIcon,
        currencySymbol = cfg.Locale.CurrencySymbol,
        points = PointsBalance(c, cust.Value.id),
        totalSpendPence = TotalSpendPence(c, cust.Value.id),
        activity = RecentActivity(c, cust.Value.id),
        rewards = ActiveRewards(c)
    });
});

// SSE stream — pushes new balance whenever a transaction touches this card
app.MapGet("/api/events/{token}", async (string token, HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // prevents Cloudflare/nginx buffering SSE
    var ch = Channel.CreateUnbounded<string>();
    var id = Guid.NewGuid();
    var subs = subscribers.GetOrAdd(token, _ => new ConcurrentDictionary<Guid, Channel<string>>());
    subs[id] = ch;
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n");
        await ctx.Response.Body.FlushAsync();
        Task<string>? pendingRead = null;
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            // Keep the same pending read across heartbeats — abandoning it would
            // orphan a ReadAsync that silently swallows the next message.
            pendingRead ??= ch.Reader.ReadAsync(ctx.RequestAborted).AsTask();
            var done = await Task.WhenAny(pendingRead, Task.Delay(25000, ctx.RequestAborted));
            if (done == pendingRead)
            {
                await ctx.Response.WriteAsync($"data: {await pendingRead}\n\n");
                pendingRead = null;
            }
            else
            {
                // Heartbeat keeps proxies/browsers from dropping the stream
                await ctx.Response.WriteAsync(": ping\n\n");
            }
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally
    {
        subs.TryRemove(id, out _);
    }
});

app.MapGet("/api/wallet/google/{token}", async (string token, HttpRequest req) =>
{
    using var c = Open();
    var issuerId = GetSetting(c, "google_wallet_issuer_id");
    var saJson = GetSetting(c, "google_wallet_service_account_json");

    if (string.IsNullOrEmpty(issuerId) || string.IsNullOrEmpty(saJson))
        return Results.Json(new
        {
            error = "Google Wallet not configured",
            howTo = "In Admin → Settings paste your Google Wallet Issuer ID and service account JSON key."
        }, statusCode: 501);

    var cust = FindCustomerByToken(c, token);
    if (cust is null) return Results.NotFound(new { error = "Unknown card" });

    var shopName = ShopName();
    var points = PointsBalance(c, cust.Value.id);
    var baseUrl = ResolvedBaseUrl(c, req);

    try
    {
        // Remember the host for background NotifyWallet calls, unless a canonical URL is set.
        RememberBaseUrl(c, req);

        var stampModules = StampModulesForPass(c, points);
        var programName2 = WalletProgramName();
        var svc = WalletService(c);
        await EnsureWalletClass(svc, shopName, programName2, baseUrl);
        var url = svc.SaveUrl(token, cust.Value.name, points, programName2, stampModules);
        return Results.Ok(new { url });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Google Wallet error: {ex.Message}" }, statusCode: 500);
    }
});

// Apple Wallet stub — needs Apple Developer account + Pass Type ID certificate
app.MapGet("/api/wallet/apple/{token}", (string token) =>
    Results.Json(new
    {
        error = "Apple Wallet not configured",
        howTo = "Requires an Apple Developer account ($99/yr) + Pass Type ID certificate. Generate a .pkpass and sign it."
    }, statusCode: 501));

// ---------------------------------------------------------------------------
// Shop (till) endpoints — guarded by staff PIN
// ---------------------------------------------------------------------------
app.MapPost("/api/shop/login", (JsonElement body) =>
{
    using var c = Open();
    var ok = body.TryGetProperty("pin", out var p) && p.GetString() == GetSetting(c, "staff_pin");
    if (!ok) return Unauthorized();
    var cfg = clientConfig.Current;
    return Results.Ok(new
    {
        ok = true,
        shopName = cfg.Identity.ShopName,
        currencySymbol = cfg.Locale.CurrencySymbol,
        currencyMinorDigits = cfg.Locale.CurrencyMinorDigits,
        quickSpend = cfg.Programme.QuickSpend,
        quickStamp = cfg.Programme.QuickStamp
    });
});

app.MapGet("/api/shop/customer/{token}", (string token, HttpRequest req) =>
{
    if (!PinOk(req, "staff_pin")) return Unauthorized();
    using var c = Open();
    var cust = FindCustomerByToken(c, token);
    if (cust is null) return Results.NotFound(new { error = "Unknown card — ask customer to re-open their loyalty card" });
    return Results.Ok(new
    {
        name = cust.Value.name,
        points = PointsBalance(c, cust.Value.id),
        rewards = ActiveRewards(c)
    });
});

app.MapPost("/api/shop/earn", (JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "staff_pin")) return Unauthorized();
    var token = body.GetProperty("token").GetString() ?? "";
    var amountPence = body.GetProperty("amountPence").GetInt64();
    var maxMinor = clientConfig.Current.Programme.MaxTransactionMinor;
    if (amountPence <= 0 || amountPence > maxMinor)
        return Results.BadRequest(new { error = $"Amount must be between {Money(1)} and {Money(maxMinor)}" });

    using var c = Open();
    var cust = FindCustomerByToken(c, token);
    if (cust is null) return Results.NotFound(new { error = "Unknown card" });

    var rate = (long)clientConfig.Current.Programme.PointsPerUnit;
    var minorPerUnit = (long)Math.Pow(10, clientConfig.Current.Locale.CurrencyMinorDigits);
    var points = amountPence * rate / minorPerUnit; // floor — e.g. 3.50 @ 10/unit = 35 pts

    var cmd = c.CreateCommand();
    cmd.CommandText = """
        INSERT INTO transactions (customer_id, type, amount_pence, points, description)
        VALUES ($id, 'earn', $amt, $pts, $desc)
        """;
    cmd.Parameters.AddWithValue("$id", cust.Value.id);
    cmd.Parameters.AddWithValue("$amt", amountPence);
    cmd.Parameters.AddWithValue("$pts", points);
    cmd.Parameters.AddWithValue("$desc", $"Purchase {Money(amountPence)}");
    cmd.ExecuteNonQuery();

    Notify(token);
    _ = NotifyWallet(token);
    return Results.Ok(new { pointsEarned = points, newBalance = PointsBalance(c, cust.Value.id), name = cust.Value.name });
});

app.MapPost("/api/shop/redeem", (JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "staff_pin")) return Unauthorized();
    var token = body.GetProperty("token").GetString() ?? "";
    var rewardId = body.GetProperty("rewardId").GetInt64();

    using var c = Open();
    var cust = FindCustomerByToken(c, token);
    if (cust is null) return Results.NotFound(new { error = "Unknown card" });

    var rcmd = c.CreateCommand();
    rcmd.CommandText = "SELECT name, points_cost FROM rewards WHERE id = $id AND active = 1";
    rcmd.Parameters.AddWithValue("$id", rewardId);
    string rewardName;
    long cost;
    using (var rr = rcmd.ExecuteReader())
    {
        if (!rr.Read()) return Results.NotFound(new { error = "Reward not found" });
        rewardName = rr.GetString(0);
        cost = rr.GetInt64(1);
    }

    var balance = PointsBalance(c, cust.Value.id);
    if (balance < cost)
        return Results.BadRequest(new { error = $"Not enough points — has {balance}, needs {cost}" });

    var cmd = c.CreateCommand();
    cmd.CommandText = """
        INSERT INTO transactions (customer_id, type, amount_pence, points, description)
        VALUES ($id, 'redeem', 0, $pts, $desc)
        """;
    cmd.Parameters.AddWithValue("$id", cust.Value.id);
    cmd.Parameters.AddWithValue("$pts", -cost);
    cmd.Parameters.AddWithValue("$desc", $"Redeemed: {rewardName}");
    cmd.ExecuteNonQuery();

    Notify(token);
    _ = NotifyWallet(token);
    return Results.Ok(new
    {
        reward = rewardName,
        newBalance = balance - cost,
        name = cust.Value.name,
        instruction = $"Apply manual discount at till: {rewardName}"
    });
});

// Stamp-card earn: 1 item = 1 point (price-independent item counting)
app.MapPost("/api/shop/stamp", (JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "staff_pin")) return Unauthorized();
    var token = body.GetProperty("token").GetString() ?? "";
    var itemCount = body.GetProperty("itemCount").GetInt64();
    var defaultNoun = clientConfig.Current.Programme.DefaultItemNoun;
    var itemName = body.TryGetProperty("itemName", out var iN) ? iN.GetString() ?? defaultNoun : defaultNoun;
    if (itemCount <= 0 || itemCount > 99)
        return Results.BadRequest(new { error = "Item count must be 1–99" });

    using var c = Open();
    var cust = FindCustomerByToken(c, token);
    if (cust is null) return Results.NotFound(new { error = "Unknown card" });

    var label = itemCount == 1 ? itemName : itemName + "s";
    var cmd = c.CreateCommand();
    cmd.CommandText = """
        INSERT INTO transactions (customer_id, type, amount_pence, points, description)
        VALUES ($id, 'earn', 0, $pts, $desc)
        """;
    cmd.Parameters.AddWithValue("$id", cust.Value.id);
    cmd.Parameters.AddWithValue("$pts", itemCount);
    cmd.Parameters.AddWithValue("$desc", $"Stamped: {itemCount} {label}");
    cmd.ExecuteNonQuery();

    Notify(token);
    _ = NotifyWallet(token);
    return Results.Ok(new { stampsEarned = itemCount, newBalance = PointsBalance(c, cust.Value.id), name = cust.Value.name });
});

// ---------------------------------------------------------------------------
// Admin endpoints — guarded by admin PIN
// ---------------------------------------------------------------------------
app.MapPost("/api/admin/login", (JsonElement body) =>
{
    using var c = Open();
    var ok = body.TryGetProperty("pin", out var p) && p.GetString() == GetSetting(c, "admin_pin");
    return ok ? Results.Ok(new { ok = true }) : Unauthorized();
});

// Dashboard: headline KPIs plus a daily and a day-of-week breakdown of signups / points earned / points redeemed
// for a selectable date range.
app.MapGet("/api/admin/dashboard", (HttpRequest req, string? range) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();

    var memberCount = ScalarLong(c, "SELECT COUNT(*) FROM customers");
    object? lastMember = null;
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, created_at FROM customers ORDER BY created_at DESC, id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (r.Read()) lastMember = new { name = r.GetString(0), createdAt = r.GetString(1) };
    }
    var totalEarned = ScalarLong(c, "SELECT COALESCE(SUM(points),0) FROM transactions WHERE type = 'earn'");
    var lastEarnedAt = ScalarText(c, "SELECT MAX(created_at) FROM transactions WHERE type = 'earn'");
    var totalRedeemed = ScalarLong(c, "SELECT COALESCE(SUM(-points),0) FROM transactions WHERE type = 'redeem'");
    var lastRedeemedAt = ScalarText(c, "SELECT MAX(created_at) FROM transactions WHERE type = 'redeem'");
    var outstanding = ScalarLong(c, "SELECT COALESCE(SUM(points),0) FROM transactions");

    // Resolve the selected date range (inclusive, calendar dates) to bucket the charts by
    var today = DateTime.UtcNow.Date;
    int MondayOffset(DateTime d) => ((int)d.DayOfWeek + 6) % 7; // days since Monday (Monday = 0)
    var thisMonday = today.AddDays(-MondayOffset(today));
    DateTime from, to = today;
    switch (range)
    {
        case "lastWeek":
            from = thisMonday.AddDays(-7);
            to = thisMonday.AddDays(-1); // last Sunday
            break;
        case "thisMonth":
            from = new DateTime(today.Year, today.Month, 1);
            break;
        case "last30":
            from = today.AddDays(-29);
            break;
        case "allTime":
            var minStr = ScalarText(c, "SELECT MIN(d) FROM (SELECT date(created_at) AS d FROM customers UNION ALL SELECT date(created_at) FROM transactions)");
            from = minStr is null ? today : DateTime.Parse(minStr);
            break;
        case "thisWeek":
        default:
            range = "thisWeek";
            from = thisMonday;
            break;
    }
    var fromStr = from.ToString("yyyy-MM-dd");
    var toStr = to.ToString("yyyy-MM-dd");

    Dictionary<string, long> GroupByDate(string sql)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$from", fromStr);
        cmd.Parameters.AddWithValue("$to", toStr);
        var map = new Dictionary<string, long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }

    // Per-calendar-day breakdown across the selected range
    var signupsByDay = GroupByDate("SELECT date(created_at) AS d, COUNT(*) FROM customers WHERE date(created_at) BETWEEN $from AND $to GROUP BY d");
    var earnedByDay = GroupByDate("SELECT date(created_at) AS d, COALESCE(SUM(points),0) FROM transactions WHERE type = 'earn' AND date(created_at) BETWEEN $from AND $to GROUP BY d");
    var redeemedByDay = GroupByDate("SELECT date(created_at) AS d, COALESCE(SUM(-points),0) FROM transactions WHERE type = 'redeem' AND date(created_at) BETWEEN $from AND $to GROUP BY d");

    var dayCount = (int)(to - from).TotalDays + 1;
    var daily = Enumerable.Range(0, dayCount).Select(i =>
    {
        var d = from.AddDays(i);
        var key = d.ToString("yyyy-MM-dd");
        return new
        {
            date = key,
            label = d.ToString("ddd d MMM"),
            signups = signupsByDay.GetValueOrDefault(key, 0),
            earned = earnedByDay.GetValueOrDefault(key, 0),
            redeemed = redeemedByDay.GetValueOrDefault(key, 0)
        };
    }).ToList();

    // Same window, totalled by weekday (Mon..Sun) instead of by calendar date
    Dictionary<int, long> GroupByWeekday(string sql)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$from", fromStr);
        cmd.Parameters.AddWithValue("$to", toStr);
        var map = new Dictionary<int, long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt32(0)] = r.GetInt64(1);
        return map;
    }
    var signupsByWeekday = GroupByWeekday("SELECT CAST(strftime('%w', created_at) AS INTEGER) AS w, COUNT(*) FROM customers WHERE date(created_at) BETWEEN $from AND $to GROUP BY w");
    var earnedByWeekday = GroupByWeekday("SELECT CAST(strftime('%w', created_at) AS INTEGER) AS w, COALESCE(SUM(points),0) FROM transactions WHERE type = 'earn' AND date(created_at) BETWEEN $from AND $to GROUP BY w");
    var redeemedByWeekday = GroupByWeekday("SELECT CAST(strftime('%w', created_at) AS INTEGER) AS w, COALESCE(SUM(-points),0) FROM transactions WHERE type = 'redeem' AND date(created_at) BETWEEN $from AND $to GROUP BY w");

    // SQLite's strftime('%w') is 0=Sunday..6=Saturday; present Monday..Sunday
    var weekdayOrder = new (string label, int w)[] { ("Mon", 1), ("Tue", 2), ("Wed", 3), ("Thu", 4), ("Fri", 5), ("Sat", 6), ("Sun", 0) };
    var byWeekday = weekdayOrder.Select(wd => new
    {
        label = wd.label,
        signups = signupsByWeekday.GetValueOrDefault(wd.w, 0),
        earned = earnedByWeekday.GetValueOrDefault(wd.w, 0),
        redeemed = redeemedByWeekday.GetValueOrDefault(wd.w, 0)
    }).ToList();

    return Results.Ok(new
    {
        memberCount, lastMember,
        totalEarned, lastEarnedAt,
        totalRedeemed, lastRedeemedAt,
        outstanding,
        range, rangeFrom = fromStr, rangeTo = toStr,
        daily, byWeekday
    });
});

app.MapGet("/api/admin/customers", (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = """
        SELECT cu.id, cu.token, cu.name, cu.email, cu.created_at,
               COALESCE(SUM(t.points),0) AS points,
               COALESCE(SUM(CASE WHEN t.type='earn' THEN t.amount_pence ELSE 0 END),0) AS spend
        FROM customers cu LEFT JOIN transactions t ON t.customer_id = cu.id
        GROUP BY cu.id ORDER BY cu.name
        """;
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new
        {
            id = r.GetInt64(0), token = r.GetString(1), name = r.GetString(2),
            email = r.IsDBNull(3) ? null : r.GetString(3), createdAt = r.GetString(4),
            points = r.GetInt64(5), totalSpendPence = r.GetInt64(6)
        });
    return Results.Ok(list);
});

// Combined activity log across all customers: signups, earns, redemptions, adjustments
app.MapGet("/api/admin/activity", (HttpRequest req, int? limit) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    var lim = Math.Clamp(limit ?? 200, 1, 1000);
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = """
        SELECT * FROM (
            SELECT cu.id AS customer_id, cu.name AS customer_name,
                   'signup' AS type, 0 AS amount_pence, 0 AS points,
                   NULL AS description, cu.created_at AS created_at
            FROM customers cu
            UNION ALL
            SELECT t.customer_id, cu.name,
                   t.type, t.amount_pence, t.points,
                   t.description, t.created_at
            FROM transactions t JOIN customers cu ON cu.id = t.customer_id
        )
        ORDER BY created_at DESC, customer_id DESC
        LIMIT $lim
        """;
    cmd.Parameters.AddWithValue("$lim", lim);
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new
        {
            customerId = r.GetInt64(0),
            customerName = r.GetString(1),
            type = r.GetString(2),
            amountPence = r.GetInt64(3),
            points = r.GetInt64(4),
            description = r.IsDBNull(5) ? null : r.GetString(5),
            createdAt = r.GetString(6)
        });
    return Results.Ok(list);
});

app.MapGet("/api/admin/customer/{id:long}", (long id, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT token, name, email, created_at FROM customers WHERE id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.NotFound(new { error = "Customer not found" });
    var result = new
    {
        id,
        token = r.GetString(0),
        name = r.GetString(1),
        email = r.IsDBNull(2) ? null : r.GetString(2),
        createdAt = r.GetString(3),
        points = PointsBalance(c, id),
        totalSpendPence = TotalSpendPence(c, id),
        activity = RecentActivity(c, id, 100)
    };
    return Results.Ok(result);
});

// Manual points adjustment (goodwill credit, corrections)
app.MapPost("/api/admin/adjust", (JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    var id = body.GetProperty("customerId").GetInt64();
    var points = body.GetProperty("points").GetInt64();
    var reason = body.TryGetProperty("reason", out var rs) ? rs.GetString() : "Manual adjustment";

    using var c = Open();
    var tcmd = c.CreateCommand();
    tcmd.CommandText = "SELECT token FROM customers WHERE id = $id";
    tcmd.Parameters.AddWithValue("$id", id);
    var token = (string?)tcmd.ExecuteScalar();
    if (token is null) return Results.NotFound(new { error = "Customer not found" });

    var cmd = c.CreateCommand();
    cmd.CommandText = """
        INSERT INTO transactions (customer_id, type, amount_pence, points, description)
        VALUES ($id, 'adjust', 0, $pts, $desc)
        """;
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$pts", points);
    cmd.Parameters.AddWithValue("$desc", reason);
    cmd.ExecuteNonQuery();
    Notify(token);
    _ = NotifyWallet(token);
    return Results.Ok(new { newBalance = PointsBalance(c, id) });
});

app.MapPut("/api/admin/customer/{id:long}", (long id, JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    var name = body.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
    var email = body.TryGetProperty("email", out var e) ? e.GetString()?.Trim() : null;
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "Name is required" });
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "UPDATE customers SET name = $name, email = $email WHERE id = $id";
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$email", (object?)(string.IsNullOrEmpty(email) ? null : email) ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$id", id);
    return cmd.ExecuteNonQuery() == 0
        ? Results.NotFound(new { error = "Customer not found" })
        : Results.Ok(new { ok = true });
});

app.MapDelete("/api/admin/customer/{id:long}", (long id, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var del1 = c.CreateCommand();
    del1.CommandText = "DELETE FROM transactions WHERE customer_id = $id";
    del1.Parameters.AddWithValue("$id", id);
    del1.ExecuteNonQuery();
    var del2 = c.CreateCommand();
    del2.CommandText = "DELETE FROM customers WHERE id = $id";
    del2.Parameters.AddWithValue("$id", id);
    return del2.ExecuteNonQuery() == 0
        ? Results.NotFound(new { error = "Customer not found" })
        : Results.Ok(new { ok = true });
});

app.MapGet("/api/admin/rewards", (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT id, name, points_cost, active, type, item_name FROM rewards ORDER BY points_cost";
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new
        {
            id = r.GetInt64(0), name = r.GetString(1), pointsCost = r.GetInt64(2), active = r.GetInt64(3) == 1,
            type = r.GetString(4), itemName = r.IsDBNull(5) ? null : r.GetString(5)
        });
    return Results.Ok(list);
});

app.MapPost("/api/admin/rewards", (JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    var name = body.GetProperty("name").GetString();
    var cost = body.GetProperty("pointsCost").GetInt64();
    var type = body.TryGetProperty("type", out var t) ? t.GetString() ?? "points" : "points";
    var itemName = body.TryGetProperty("itemName", out var iN) ? iN.GetString() : null;
    if (string.IsNullOrWhiteSpace(name) || cost <= 0) return Results.BadRequest(new { error = "Name and positive points cost required" });
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "INSERT INTO rewards (name, points_cost, type, item_name) VALUES ($n, $p, $t, $i)";
    cmd.Parameters.AddWithValue("$n", name);
    cmd.Parameters.AddWithValue("$p", cost);
    cmd.Parameters.AddWithValue("$t", type);
    cmd.Parameters.AddWithValue("$i", (object?)itemName ?? DBNull.Value);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { ok = true });
});

app.MapPut("/api/admin/rewards/{id:long}", (long id, JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var cmd = c.CreateCommand();
    var itemName = body.TryGetProperty("itemName", out var iN) ? iN.GetString() : null;
    cmd.CommandText = "UPDATE rewards SET name = $n, points_cost = $p, active = $a, type = $t, item_name = $i WHERE id = $id";
    cmd.Parameters.AddWithValue("$n", body.GetProperty("name").GetString());
    cmd.Parameters.AddWithValue("$p", body.GetProperty("pointsCost").GetInt64());
    cmd.Parameters.AddWithValue("$a", body.GetProperty("active").GetBoolean() ? 1 : 0);
    cmd.Parameters.AddWithValue("$t", body.TryGetProperty("type", out var tp) ? tp.GetString() ?? "points" : "points");
    cmd.Parameters.AddWithValue("$i", (object?)itemName ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$id", id);
    return cmd.ExecuteNonQuery() == 1 ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Reward not found" });
});

app.MapGet("/api/admin/settings", (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    var cfg = clientConfig.Current;
    return Results.Ok(new
    {
        shopName = cfg.Identity.ShopName,
        // Derived when blank, so the field shows what the pass will actually say.
        walletProgramName = WalletProgramName(),
        pointsPerPound = cfg.Programme.PointsPerUnit.ToString(),
        staffPin = GetSetting(c, "staff_pin"),
        googleWalletIssuerId = GetSetting(c, "google_wallet_issuer_id"),
        googleWalletConfigured = !string.IsNullOrEmpty(GetSetting(c, "google_wallet_service_account_json"))
    });
});

app.MapPut("/api/admin/settings", (JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    using var c = Open();
    void Set(string key, string value)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
    if (body.TryGetProperty("shopName", out var sn)) Set("shop_name", sn.GetString() ?? "");
    if (body.TryGetProperty("walletProgramName", out var wpn) && !string.IsNullOrEmpty(wpn.GetString())) Set("wallet_program_name", wpn.GetString()!);
    if (body.TryGetProperty("pointsPerPound", out var pp)) Set("points_per_pound", pp.GetString() ?? "10");
    if (body.TryGetProperty("staffPin", out var sp) && !string.IsNullOrEmpty(sp.GetString())) Set("staff_pin", sp.GetString()!);
    if (body.TryGetProperty("adminPin", out var ap) && !string.IsNullOrEmpty(ap.GetString())) Set("admin_pin", ap.GetString()!);
    if (body.TryGetProperty("googleWalletIssuerId", out var gi)) Set("google_wallet_issuer_id", gi.GetString() ?? "");
    if (body.TryGetProperty("googleWalletServiceAccountJson", out var gj) && !string.IsNullOrEmpty(gj.GetString())) Set("google_wallet_service_account_json", gj.GetString()!);
    clientConfig.Invalidate();
    return Results.Ok(new { ok = true });
});

// ---------------------------------------------------------------------------
// Branding: theme, assets, and the admin surface that edits them
// ---------------------------------------------------------------------------

// The client's palette as CSS custom properties. Every page links this; no page carries
// a literal hex value, which is what makes a re-skin a settings change.
app.MapGet("/theme.css", (HttpResponse res) =>
{
    res.Headers.ETag = $"W/\"theme-{clientConfig.Version}\"";
    res.Headers.CacheControl = "no-cache";
    return Results.Content(ThemeCss.Render(clientConfig.Current), "text/css; charset=utf-8");
});

// Brand assets, resolved platform default -> seeded -> client upload. URLs carry a content
// hash, so these can be cached hard and still update the instant a client swaps their logo.
app.MapGet("/brand/{name}", (string name, HttpResponse res) =>
{
    var path = assets.Resolve(name);
    if (path is null) return Results.NotFound();
    res.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(path, AssetResolver.ContentType(path));
});

app.MapGet("/api/admin/branding", (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    var c = clientConfig.Current;
    return Results.Ok(new
    {
        config = c,
        presets = ThemePresets.All.Select(p => new { p.Id, p.Name, p.Description, theme = p.Theme }),
        assets = AssetResolver.Names.Select(n => new
        {
            name = n,
            url = assets.Url(n),
            custom = assets.IsCustom(n)
        }),
        // Surfaced so the Branding tab can warn before a client makes their own site unreadable.
        contrast = new
        {
            accentOnPrimary = Math.Round(ThemeCss.ContrastRatio(c.Theme.Accent, c.Theme.BrandPrimary), 2),
            onAccentOnAccent = Math.Round(ThemeCss.ContrastRatio(c.Theme.OnAccent, c.Theme.Accent), 2),
            whiteOnPrimaryDeep = Math.Round(ThemeCss.ContrastRatio("#ffffff", c.Theme.BrandPrimaryDeep), 2),
            primaryOnSurface = Math.Round(ThemeCss.ContrastRatio(c.Theme.BrandPrimary, c.Theme.Surface), 2)
        },
        version = clientConfig.Version
    });
});

// Accepts dotted config paths, e.g. { "theme.accent": "#ff0000", "copy.joinPill": "Rewards" }.
// Unknown paths are ignored by the config layer rather than inventing settings.
app.MapPut("/api/admin/branding", (JsonElement body, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "Expected an object" });

    var known = ConfigStore.Flatten(clientConfig.Current);
    var updates = new List<KeyValuePair<string, string>>();
    var rejected = new List<string>();
    foreach (var prop in body.EnumerateObject())
    {
        if (!known.ContainsKey(prop.Name)) { rejected.Add(prop.Name); continue; }
        var value = prop.Value.ValueKind switch
        {
            JsonValueKind.String => prop.Value.GetString() ?? "",
            JsonValueKind.Null => "",
            _ => prop.Value.GetRawText()
        };
        updates.Add(new(prop.Name, value));
    }
    if (updates.Count == 0 && rejected.Count > 0)
        return Results.BadRequest(new { error = "No recognised settings", rejected });

    using var c = Open();
    SaveSettings(c, updates);
    return Results.Ok(new { ok = true, updated = updates.Count, rejected, version = clientConfig.Version });
});

// Applies a whole palette at once. Individual tokens can still be overridden afterwards.
app.MapPost("/api/admin/branding/preset/{id}", (string id, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    var preset = ThemePresets.Find(id);
    if (preset is null) return Results.NotFound(new { error = "Unknown preset" });

    var values = ConfigStore.Flatten(new ClientConfig { Theme = preset.Theme })
        .Where(kv => kv.Key.StartsWith("theme.", StringComparison.Ordinal))
        .ToList();
    using var c = Open();
    SaveSettings(c, values);
    return Results.Ok(new { ok = true, preset = preset.Id, version = clientConfig.Version });
});

// Uploads are resized, stripped of EXIF and re-encoded before they touch disk; replacing
// the logo regenerates the PWA icon set.
app.MapPost("/api/admin/branding/asset/{name}", async (string name, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "Expected a file upload" });

    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No file supplied" });

    await using var stream = file.OpenReadStream();
    var result = await ImagePipeline.SaveAsync(assets, name, stream, file.ContentType ?? "");
    if (!result.Ok) return Results.BadRequest(new { error = result.Error });

    clientConfig.Invalidate(); // drops the compiled-page cache so new asset URLs are emitted
    return Results.Ok(new { ok = true, url = assets.Url(name), result.Width, result.Height, result.Bytes });
});

app.MapDelete("/api/admin/branding/asset/{name}", (string name, HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    if (!AssetResolver.IsKnown(name)) return Results.NotFound(new { error = "Unknown asset" });
    ImagePipeline.Revert(assets, name);
    clientConfig.Invalidate();
    return Results.Ok(new { ok = true, url = assets.Url(name) });
});

// Dumps live config as the JSON you commit back to clients/{slug}/client.json — the backup,
// rollback and clone-this-client path.
app.MapGet("/api/admin/config-export", (HttpRequest req) =>
{
    if (!PinOk(req, "admin_pin")) return Unauthorized();
    var json = JsonSerializer.Serialize(clientConfig.Current, ConfigStore.Json);
    return Results.Text(json, "application/json");
});

// ---------------------------------------------------------------------------
// Static pages — the four HTML pages render through the template layer, so endpoints
// are mapped for each path. StaticFileMiddleware skips a request once an endpoint matches.
// ---------------------------------------------------------------------------
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/join.html", () => Page("join.html"));
app.MapGet("/shop", () => Page("shop/index.html"));          // also matches /shop/
app.MapGet("/shop/index.html", () => Page("shop/index.html"));
app.MapGet("/admin", () => Page("admin/index.html"));        // also matches /admin/
app.MapGet("/admin/index.html", () => Page("admin/index.html"));
app.MapGet("/sw.js", () => Templated("sw.js", "text/javascript; charset=utf-8"));
app.MapGet("/shop/sw.js", () => Templated("shop/sw.js", "text/javascript; charset=utf-8"));

// PWA manifests, rendered per client rather than shipped as static JSON.
app.MapGet("/manifest.json", () =>
{
    var c = clientConfig.Current;
    return Results.Content(JsonSerializer.Serialize(new
    {
        name = c.Copy.PwaName,
        short_name = c.Copy.PwaShortName,
        description = c.Copy.PwaDescription,
        start_url = "/",
        display = "standalone",
        background_color = c.Theme.BrandPrimaryDeep,
        theme_color = c.Theme.BrandPrimary,
        icons = new[]
        {
            new { src = assets.Url("icon192"), sizes = "192x192", type = "image/png", purpose = "any" },
            new { src = assets.Url("icon512"), sizes = "512x512", type = "image/png", purpose = "any" },
            new { src = assets.Url("iconMaskable"), sizes = "512x512", type = "image/png", purpose = "maskable" }
        }
    }), "application/manifest+json");
});

app.MapGet("/shop/manifest.json", () =>
{
    var c = clientConfig.Current;
    return Results.Content(JsonSerializer.Serialize(new
    {
        name = c.Copy.PwaTillName,
        short_name = c.Copy.PwaTillShortName,
        start_url = "/shop/",
        display = "standalone",
        background_color = c.Theme.BrandPrimary,
        theme_color = c.Theme.BrandPrimary,
        icons = new[]
        {
            new { src = assets.Url("icon192"), sizes = "192x192", type = "image/png", purpose = "any" },
            new { src = assets.Url("icon512"), sizes = "512x512", type = "image/png", purpose = "any maskable" }
        }
    }), "application/manifest+json");
});

// /card/{token} serves the customer card page (token read client-side from URL)
app.MapGet("/card/{token}", () => Page("card.html"));

// Dynamic manifest for each customer's card (start_url must match the token URL for PWA install)
app.MapGet("/api/manifest/{token}", (string token) =>
{
    var cfg = clientConfig.Current;
    var manifest = System.Text.Json.JsonSerializer.Serialize(new
    {
        name = cfg.Copy.PwaCardName,
        short_name = cfg.Copy.PwaCardShortName,
        start_url = $"/card/{token}",
        display = "standalone",
        background_color = cfg.Theme.SurfaceDark,
        theme_color = cfg.Theme.BrandPrimary,
        icons = new[]
        {
            new { src = assets.Url("icon192"), sizes = "192x192", type = "image/png", purpose = "any" },
            new { src = assets.Url("icon512"), sizes = "512x512", type = "image/png", purpose = "any" }
        }
    });
    return Results.Content(manifest, "application/manifest+json");
});

app.MapGet("/", () => Results.Redirect("/join.html"));

app.Run();
