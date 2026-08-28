using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

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
            ('shop_name', 'Queen of the South Café'),
            ('wallet_program_name', 'QOSFC Loyalty'),
            ('points_per_pound', '10'),
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

string GetSetting(SqliteConnection c, string key)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
    cmd.Parameters.AddWithValue("$k", key);
    return (string?)cmd.ExecuteScalar() ?? "";
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
    var rate = long.TryParse(GetSetting(c, "points_per_pound"), out var rr) ? rr : 10;
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
        var slots = r.GetInt64(1);
        var itemName = r.IsDBNull(2) ? "item" : r.GetString(2);
        var filled = Math.Min(points, slots);
        var grid = string.Concat(Enumerable.Repeat("🏆", (int)filled))
                 + string.Concat(Enumerable.Repeat("○", (int)(slots - filled)));
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
        var shopName = GetSetting(c, "shop_name");
        var points = PointsBalance(c, cust.Value.id);
        var stampModules = StampModulesForPass(c, points);

        var svc = new GoogleWalletService(issuerId, saJson);
        var publicBase = GetSetting(c, "public_base_url");
        var programName = GetSetting(c, "wallet_program_name");
        if (string.IsNullOrEmpty(programName)) programName = $"{shopName} Loyalty";
        await svc.EnsureClassExists(shopName, programName, publicBase);
        await svc.UpsertObject(token, cust.Value.name, points, shopName, stampModules);
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

    var baseUrl = $"{req.Scheme}://{req.Host}";
    // Persist for future background calls
    var sc2 = c.CreateCommand();
    sc2.CommandText = "INSERT INTO settings (key,value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
    sc2.Parameters.AddWithValue("$k", "public_base_url"); sc2.Parameters.AddWithValue("$v", baseUrl);
    sc2.ExecuteNonQuery();

    try
    {
        var shopName = GetSetting(c, "shop_name");
        var programName = GetSetting(c, "wallet_program_name");
        if (string.IsNullOrEmpty(programName)) programName = $"{shopName} Loyalty";
        var svc = new GoogleWalletService(issuerId, saJson);
        await svc.EnsureClassExists(shopName, programName, baseUrl);
        return Results.Ok(new { ok = true, baseUrl });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
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
        var svc = new GoogleWalletService(issuerId, saJson);
        var baseUrl = $"{req.Scheme}://{req.Host}";
        var shopName = GetSetting(c, "shop_name");
        var result = await svc.TestConnection(shopName, baseUrl);
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
        var svc = new GoogleWalletService(issuerId, saJson);
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
    var rate = long.TryParse(GetSetting(c, "points_per_pound"), out var rr) ? rr : 10;
    return Results.Ok(new
    {
        name = cust.Value.name,
        shopName = GetSetting(c, "shop_name"),
        pointsPerPound = rate,
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

    var shopName = GetSetting(c, "shop_name");
    var points = PointsBalance(c, cust.Value.id);
    var baseUrl = $"{req.Scheme}://{req.Host}";

    try
    {
        // Persist the public URL so background NotifyWallet calls use the real hostname
        void SaveSetting(string key, string val) {
            var sc = c.CreateCommand();
            sc.CommandText = "INSERT INTO settings (key,value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
            sc.Parameters.AddWithValue("$k", key); sc.Parameters.AddWithValue("$v", val);
            sc.ExecuteNonQuery();
        }
        if (!string.IsNullOrEmpty(baseUrl)) SaveSetting("public_base_url", baseUrl);

        var stampModules = StampModulesForPass(c, points);
        var programName2 = GetSetting(c, "wallet_program_name");
        if (string.IsNullOrEmpty(programName2)) programName2 = $"{shopName} Loyalty";
        var svc = new GoogleWalletService(issuerId, saJson);
        await svc.EnsureClassExists(shopName, programName2, baseUrl);
        var url = svc.SaveUrl(token, cust.Value.name, points, shopName, stampModules);
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
    return ok ? Results.Ok(new { ok = true, shopName = GetSetting(c, "shop_name") }) : Unauthorized();
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
    if (amountPence <= 0 || amountPence > 100000)
        return Results.BadRequest(new { error = "Amount must be between £0.01 and £1000" });

    using var c = Open();
    var cust = FindCustomerByToken(c, token);
    if (cust is null) return Results.NotFound(new { error = "Unknown card" });

    var rate = long.Parse(GetSetting(c, "points_per_pound"));
    var points = amountPence * rate / 100; // floor — e.g. £3.50 @ 10/£ = 35 pts

    var cmd = c.CreateCommand();
    cmd.CommandText = """
        INSERT INTO transactions (customer_id, type, amount_pence, points, description)
        VALUES ($id, 'earn', $amt, $pts, $desc)
        """;
    cmd.Parameters.AddWithValue("$id", cust.Value.id);
    cmd.Parameters.AddWithValue("$amt", amountPence);
    cmd.Parameters.AddWithValue("$pts", points);
    cmd.Parameters.AddWithValue("$desc", $"Purchase £{amountPence / 100.0:0.00}");
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
    var itemName = body.TryGetProperty("itemName", out var iN) ? iN.GetString() ?? "item" : "item";
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
    return Results.Ok(new
    {
        shopName = GetSetting(c, "shop_name"),
        walletProgramName = GetSetting(c, "wallet_program_name"),
        pointsPerPound = GetSetting(c, "points_per_pound"),
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
    return Results.Ok(new { ok = true });
});

// ---------------------------------------------------------------------------
// Static pages
// ---------------------------------------------------------------------------
app.UseDefaultFiles();
app.UseStaticFiles();

// /card/{token} serves the customer card page (token read client-side from URL)
app.MapGet("/card/{token}", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "card.html"), "text/html"));

// Dynamic manifest for each customer's card (start_url must match the token URL for PWA install)
app.MapGet("/api/manifest/{token}", (string token) =>
{
    var manifest = System.Text.Json.JsonSerializer.Serialize(new
    {
        name = "QOSFC Loyalty Card",
        short_name = "My Card",
        start_url = $"/card/{token}",
        display = "standalone",
        background_color = "#061d33",
        theme_color = "#094582",
        icons = new[]
        {
            new { src = "/logo.png", sizes = "192x192", type = "image/png", purpose = "any" },
            new { src = "/logo.png", sizes = "512x512", type = "image/png", purpose = "any" }
        }
    });
    return Results.Content(manifest, "application/manifest+json");
});

app.MapGet("/", () => Results.Redirect("/join.html"));

app.Run();
