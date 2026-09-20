using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CoffeeLoyalty.Branding;

/// <summary>
/// Resolves a logical brand asset name to a file on disk, walking the fallback chain:
///
///   wwwroot platform default  ->  config/assets/ (seeded)  ->  data/assets/ (client upload, wins)
///
/// Uploads land on the persisted data volume, not the read-only config mount, so they
/// survive image upgrades. Every URL carries a content hash so a client who swaps their
/// logo sees the new one immediately rather than a cached copy.
/// </summary>
public sealed class AssetResolver
{
    /// <summary>Logical name -> the wwwroot file that ships with the app.</summary>
    private static readonly Dictionary<string, string> PlatformDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logo"] = "logo.png",
        ["hero"] = "latte.png",
        ["favicon"] = "logo.png",
        ["icon192"] = "logo.png",
        ["icon512"] = "logo.png",
        ["iconMaskable"] = "logo.png",
        ["walletLogo"] = "logo.jpg"
    };

    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp", ".svg" };

    private readonly string _uploadDir;
    private readonly string _seedDir;
    private readonly string _webRoot;
    private readonly ConcurrentDictionary<string, (long Ticks, long Length, string Hash)> _hashes = new();

    public AssetResolver(string uploadDir, string seedDir, string webRoot)
    {
        _uploadDir = uploadDir;
        _seedDir = seedDir;
        _webRoot = webRoot;
        Directory.CreateDirectory(_uploadDir);
    }

    public static IReadOnlyCollection<string> Names => PlatformDefaults.Keys;

    public static bool IsKnown(string name) => PlatformDefaults.ContainsKey(name);

    public string UploadPath(string name, string extension) =>
        Path.Combine(_uploadDir, name + extension);

    /// <summary>Highest-priority existing file for this asset, or null if the name is unknown.</summary>
    public string? Resolve(string name)
    {
        if (!PlatformDefaults.TryGetValue(name, out var fallback)) return null;

        foreach (var dir in new[] { _uploadDir, _seedDir })
        {
            foreach (var ext in Extensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        var platform = Path.Combine(_webRoot, fallback);
        return File.Exists(platform) ? platform : null;
    }

    /// <summary>True when the client has uploaded their own version (so the UI can offer a reset).</summary>
    public bool IsCustom(string name) =>
        Extensions.Any(ext => File.Exists(Path.Combine(_uploadDir, name + ext)));

    public void RemoveUpload(string name)
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_uploadDir, name + ext);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Short content hash, recomputed only when the file's timestamp or length moves.</summary>
    public string Hash(string name)
    {
        var path = Resolve(name);
        if (path is null) return "0";

        var info = new FileInfo(path);
        var key = path;
        if (_hashes.TryGetValue(key, out var cached)
            && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length)
            return cached.Hash;

        string hash;
        try
        {
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(stream))[..8].ToLowerInvariant();
        }
        catch (IOException)
        {
            return "0";
        }

        _hashes[key] = (info.LastWriteTimeUtc.Ticks, info.Length, hash);
        return hash;
    }

    /// <summary>The URL pages and manifests reference, e.g. /brand/logo?v=1a2b3c4d.</summary>
    public string Url(string name) => $"/brand/{name}?v={Hash(name)}";

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
}
