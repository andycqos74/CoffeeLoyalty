// Editable words and photos on the customer-facing pages (join page, loyalty card, privacy policy). The fields are
// the same for every theme; each theme supplies their default values in theme.json "content". An admin edits them
// under Admin -> Pages; saved values live in the content table, and a field that was never edited (or was reset)
// shows the theme's current default.
//
// Text formatting, the same convention as the Gleam site engine:
//   [words]    highlighted in the theme's accent colour
//   *words*    italic          **words**  bold
//   new line   line break (headline and multi-line fields)
// Privacy text: a line starting "## " is a heading; blank lines separate paragraphs.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

public enum FieldType { Text, Headline, Textarea, Rich, Image }

public sealed record PageField(string Key, string Page, string Label, FieldType Type, string? Help = null, bool Required = true);

public static class PageContent
{
    public static readonly string[] Pages = ["brand", "join", "card", "privacy"];
    public static readonly Dictionary<string, string> PageLabels = new()
    {
        ["brand"] = "Every page",
        ["join"] = "Join page",
        ["card"] = "Loyalty card",
        ["privacy"] = "Privacy policy",
    };

    public static readonly PageField[] Fields =
    [
        new("brand.name", "brand", "Name shown beside the logo", FieldType.Text),
        new("brand.logo", "brand", "Logo", FieldType.Image, "Square works best. Also used for the home-screen icon and the Google Wallet pass."),

        new("join.kicker", "join", "Small label above the headline", FieldType.Text),
        new("join.headline", "join", "Headline", FieldType.Headline, "Put [square brackets] round words to highlight them, *stars* for italics. Each line is a new line."),
        new("join.subcopy", "join", "Text under the headline", FieldType.Textarea),
        new("join.photo", "join", "Photo", FieldType.Image, "Shown behind the sign-up form. Can be animated."),
        new("join.nameLabel", "join", "Name box label", FieldType.Text),
        new("join.emailLabel", "join", "Email box label", FieldType.Text),
        new("join.consent", "join", "Marketing opt-in wording", FieldType.Textarea),
        new("join.button", "join", "Join button", FieldType.Text),
        new("join.already", "join", "Link for people who already joined", FieldType.Text),

        new("card.greeting", "card", "Greeting above the customer's name", FieldType.Text),
        new("card.qrLabel", "card", "Label above the QR code", FieldType.Text),
        new("card.readyTitle", "card", "Reward ready title", FieldType.Text, "{reward} is replaced with the reward's name."),
        new("card.readySub", "card", "Reward ready text", FieldType.Text),
        new("card.pointsTitle", "card", "Points rewards heading", FieldType.Text),
        new("card.activityTitle", "card", "Recent activity heading", FieldType.Text),
        new("card.emptyActivity", "card", "Shown when there's no activity yet", FieldType.Text),
        new("card.tip", "card", "Tip at the bottom", FieldType.Text, "**Stars** make words bold."),
        new("card.photo", "card", "Photo (optional)", FieldType.Image, "Shown faintly at the foot of the card, if the theme uses it. Can be animated.", Required: false),

        new("privacy.title", "privacy", "Title", FieldType.Text),
        new("privacy.body", "privacy", "Policy text", FieldType.Rich, "A line starting ## is a heading. Leave a blank line between paragraphs."),
    ];

    public static PageField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);

    public const int MaxLength = 20000;

    // ---------------------------------------------------------------- storage

    public static void InitTable(SqliteConnection c)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS content (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL DEFAULT (datetime('now')))";
        cmd.ExecuteNonQuery();
    }

    public static Dictionary<string, string> Saved(SqliteConnection c)
    {
        var map = new Dictionary<string, string>();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM content";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    // Current value of every field: saved, else the theme's default.
    public static Dictionary<string, string> Values(SqliteConnection c, Theme theme, IReadOnlyDictionary<string, string>? draft = null)
    {
        var saved = Saved(c);
        var values = Fields.ToDictionary(f => f.Key, f => saved.TryGetValue(f.Key, out var v) ? v : theme.Content.GetValueOrDefault(f.Key, ""));
        if (draft is not null)
            foreach (var (k, v) in draft)
                if (values.ContainsKey(k)) values[k] = v;
        return values;
    }

    // Unsaved edits from the admin's preview: key -> value (null = theme default).
    public static Dictionary<string, string> Draft(JsonElement values, Theme theme)
    {
        var draft = new Dictionary<string, string>();
        if (values.ValueKind != JsonValueKind.Object) return draft;
        foreach (var p in values.EnumerateObject())
        {
            var f = Field(p.Name);
            if (f is null) continue;
            var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : theme.Content.GetValueOrDefault(f.Key, "");
            if (f.Type == FieldType.Image && v != "" && !IsImageUrl(v)) continue;
            draft[f.Key] = v.Replace("\r\n", "\n");
        }
        return draft;
    }

    // Validates and saves edits. A null value resets the field to the theme default.
    public static void Save(SqliteConnection c, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object) throw new ArgumentException("Nothing to save.");
        using var tx = c.BeginTransaction();
        foreach (var p in values.EnumerateObject())
        {
            var f = Field(p.Name) ?? throw new ArgumentException($"Unknown field: {p.Name}");
            var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$k", f.Key);
            if (p.Value.ValueKind == JsonValueKind.Null)
            {
                cmd.CommandText = "DELETE FROM content WHERE key = $k";
            }
            else
            {
                var v = (p.Value.GetString() ?? "").Replace("\r\n", "\n");
                if (v.Length > MaxLength) throw new ArgumentException($"{f.Label} is too long.");
                if (f.Type == FieldType.Image && v != "" && !IsImageUrl(v))
                    throw new ArgumentException($"{f.Label}: choose an uploaded photo or one that comes with the theme.");
                if (f.Type is FieldType.Text) v = v.Replace("\n", " ").Trim();
                cmd.CommandText = "INSERT INTO content (key, value, updated_at) VALUES ($k, $v, datetime('now')) ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = datetime('now')";
                cmd.Parameters.AddWithValue("$v", v);
            }
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // Photos are either uploads (/media/<file>) or files that ship with the theme (/img/..., /logo.png etc.).
    public static bool IsImageUrl(string v) =>
        !v.Contains("..") && Regex.IsMatch(v, @"^/(media/[\w-]+\.(webp|jpe?g|png)|[\w/-]+(\.[\w-]+)*\.(webp|jpe?g|png|svg|gif))$", RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------- formatting

    public static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    // Inline formatting for one line: [accent], **bold**, *italic*. Input is escaped first.
    public static string Inline(string s)
    {
        var h = Esc(s);
        h = Regex.Replace(h, @"\[(.+?)\]", "<span class=\"accent\">$1</span>");
        h = Regex.Replace(h, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        h = Regex.Replace(h, @"\*(.+?)\*", "<em>$1</em>");
        // Email addresses become mailto links
        h = Regex.Replace(h, @"\b[\w.+-]+@[\w-]+(\.[\w-]+)+\b", m => $"<a href=\"mailto:{m.Value}\">{m.Value}</a>");
        return h;
    }

    public static string Lines(string s) => string.Join("<br>", s.Split('\n').Select(Inline));

    public static string Rich(string s)
    {
        var sb = new StringBuilder();
        foreach (var block in Regex.Split(s.Trim(), @"\n\s*\n"))
        {
            var para = new List<string>();
            void Flush()
            {
                if (para.Count == 0) return;
                sb.Append("<p>").Append(string.Join("<br>", para)).Append("</p>");
                para.Clear();
            }
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("## ")) { Flush(); sb.Append("<h3>").Append(Inline(line[3..].Trim())).Append("</h3>"); }
                else if (line.Trim() != "") para.Add(Inline(line.Trim()));
            }
            Flush();
        }
        return sb.ToString();
    }

    public static string Format(PageField f, string v) => f.Type switch
    {
        FieldType.Rich => Rich(v),
        FieldType.Headline or FieldType.Textarea => Lines(v),
        _ => Inline(v),
    };
}
