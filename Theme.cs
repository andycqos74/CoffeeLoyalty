// Themes: the look of the customer-facing pages (join + card), their starting words and photos, and the brand
// colours used for the PWA manifest and the Google Wallet pass. The engine (this app) is the same for every
// deployment; THEME picks the folder in themes/ (default "qosfc"), or THEME_DIR points at a theme folder mounted
// into the container. Each deployment runs one theme in its own container with its own /app/data volume.
//
//   themes/<name>/theme.json   name, label, colours, fonts, stamp icon, default settings and default page content
//   themes/<name>/public/      theme.css, logo and images, served at the site root (e.g. /theme.css, /img/x.jpg)
//
// See README "Themes" for how to add one.

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class ThemeColours
{
    // Browser chrome / PWA theme colour
    public string ThemeColor { get; set; } = "#094582";
    // PWA splash background
    public string Background { get; set; } = "#052038";
    // Google Wallet pass background
    public string Wallet { get; set; } = "#094582";
}

public sealed class Theme
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Lang { get; set; } = "en-GB";
    public ThemeColours Colours { get; set; } = new();
    // A Google Fonts stylesheet URL (optional)
    public string? FontsUrl { get; set; }
    // Inline SVG for a stamp slot. Use class="s-body" for the filled shape and class="s-line" for strokes so the
    // base stylesheet can colour empty and filled stamps from the theme's CSS variables.
    public string StampIcon { get; set; } = "";
    // Inline SVG shown in the "reward ready" banner (uses currentColor)
    public string RewardIcon { get; set; } = "";
    // Settings seeded into a new database (shop_name, wallet_program_name, ...). Never overwrite saved values.
    public Dictionary<string, string> Settings { get; set; } = new();
    // Default value of every editable page field (see PageContent.Fields). Unedited fields always show the theme's
    // current default, so changing a default here reaches running sites that haven't edited that field.
    public Dictionary<string, string> Content { get; set; } = new();

    [JsonIgnore] public string Dir { get; private set; } = "";
    [JsonIgnore] public string PublicDir => Path.Combine(Dir, "public");

    public static Theme Load(IConfiguration config)
    {
        var dirOverride = config["THEME_DIR"];
        var name = config["THEME"];
        if (string.IsNullOrWhiteSpace(name)) name = "qosfc";
        string dir;
        if (!string.IsNullOrWhiteSpace(dirOverride)) dir = Path.GetFullPath(dirOverride);
        else
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\w-]+$"))
                throw new InvalidOperationException($"THEME \"{name}\" is not a valid theme name.");
            dir = Path.Combine(AppContext.BaseDirectory, "themes", name);
        }
        var file = Path.Combine(dir, "theme.json");
        if (!File.Exists(file))
            throw new InvalidOperationException($"No theme found at {file}. Set THEME to a folder in themes/ or THEME_DIR to a theme folder.");

        var theme = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException($"{file} is empty.");
        theme.Dir = dir;

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(theme.Name)) problems.Add("name");
        if (!File.Exists(Path.Combine(theme.PublicDir, "theme.css"))) problems.Add("public/theme.css");
        if (string.IsNullOrWhiteSpace(theme.StampIcon)) problems.Add("stampIcon");
        foreach (var f in PageContent.Fields.Where(f => f.Required && !theme.Content.ContainsKey(f.Key)))
            problems.Add($"content.{f.Key}");
        if (problems.Count > 0)
            throw new InvalidOperationException($"Theme in {dir} is missing: {string.Join(", ", problems)}");
        if (string.IsNullOrWhiteSpace(theme.Label)) theme.Label = theme.Name;
        return theme;
    }
}
