using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CoffeeLoyalty.Config;

/// <summary>
/// Resolves the effective <see cref="ClientConfig"/> by layering three sources:
///
///   1. code defaults         — always complete, so nothing can render blank
///   2. config/client.json    — the provisioning seed, mounted read-only per container
///   3. DB `settings` rows    — dotted keys written by the admin Branding tab; these win
///
/// The database is the source of truth: client.json only gets a new establishment to a
/// presentable state on first boot. Bump <see cref="Version"/> on every save so the
/// theme.css, compiled-page and asset caches all drop together.
/// </summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // Legacy flat keys predating the config tree. They stay canonical so an existing
    // install keeps its settings and the original admin Settings tab keeps working.
    private static readonly (string LegacyKey, string Path)[] LegacyMap =
    {
        ("shop_name",           "identity.shopName"),
        ("wallet_program_name", "wallet.programName"),
        ("points_per_pound",    "programme.pointsPerUnit")
    };

    private readonly string _seedPath;
    private readonly Func<SqliteConnection> _open;
    private readonly object _lock = new();
    private ClientConfig? _cached;
    private int _version;

    public ConfigStore(string configDir, Func<SqliteConnection> open)
    {
        _seedPath = Path.Combine(configDir, "client.json");
        _open = open;
    }

    /// <summary>Changes whenever config or assets change; used as a cache key and asset cache-buster.</summary>
    public int Version { get { lock (_lock) { return _version; } } }

    public ClientConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _cached ??= Build();
            }
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _cached = null;
            _version++;
        }
    }

    private ClientConfig Build()
    {
        var tree = JsonSerializer.SerializeToNode(new ClientConfig(), Json)!.AsObject();

        if (File.Exists(_seedPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(_seedPath)) is JsonObject seed)
                    DeepMerge(tree, seed);
            }
            catch (JsonException)
            {
                // A malformed seed must not take the app down — defaults still render.
            }
        }

        foreach (var (key, value) in ReadSettings())
        {
            if (key.Contains('.')) SetPath(tree, key, value);
        }

        // Legacy keys are applied last so the original Settings tab always wins for the
        // three values it owns.
        var legacy = ReadSettings();
        foreach (var (legacyKey, path) in LegacyMap)
        {
            if (legacy.TryGetValue(legacyKey, out var v) && !string.IsNullOrWhiteSpace(v))
                SetPath(tree, path, v);
        }

        return tree.Deserialize<ClientConfig>(Json) ?? new ClientConfig();
    }

    private Dictionary<string, string> ReadSettings()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var c = _open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM settings";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        }
        catch (SqliteException)
        {
            // First run, before InitDb — defaults are correct here.
        }
        return map;
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is JsonObject childSource && target[key] is JsonObject childTarget)
                DeepMerge(childTarget, childSource);
            else
                target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Writes a dotted path into the tree, coercing the raw string to whatever type the
    /// default at that path already is — so "12" lands as a number and "[1,2]" as an array.
    /// </summary>
    private static void SetPath(JsonObject root, string dottedPath, string raw)
    {
        var parts = dottedPath.Split('.');
        var node = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is not JsonObject next) return; // unknown section — ignore
            node = next;
        }

        var leaf = parts[^1];
        var existing = node[leaf];
        if (existing is null) return; // unknown key — ignore rather than inventing config

        node[leaf] = Coerce(existing, raw);
    }

    private static JsonNode? Coerce(JsonNode existing, string raw)
    {
        try
        {
            if (existing is JsonArray) return JsonNode.Parse(raw);
            if (existing is JsonValue v && v.TryGetValue<bool>(out _)) return JsonValue.Create(bool.Parse(raw));
            if (existing is JsonValue n && n.TryGetValue<double>(out _)) return JsonValue.Create(double.Parse(raw));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return existing.DeepClone(); // bad value — keep the default rather than break the page
        }
        return JsonValue.Create(raw);
    }

    /// <summary>Flattens the live config to dotted key/value pairs for the admin UI and config-export.</summary>
    public static Dictionary<string, string> Flatten(ClientConfig config)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(JsonSerializer.SerializeToNode(config, Json)!.AsObject(), "", flat);
        return flat;
    }

    private static void Walk(JsonObject obj, string prefix, Dictionary<string, string> into)
    {
        foreach (var (key, value) in obj)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (value is JsonObject child) Walk(child, path, into);
            else if (value is JsonArray arr) into[path] = arr.ToJsonString();
            else into[path] = value?.GetValue<object>()?.ToString() ?? "";
        }
    }
}
