using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using CoffeeLoyalty.Config;

namespace CoffeeLoyalty.Branding;

/// <summary>
/// Serves the static HTML pages with {{token}} placeholders substituted from the client's
/// config. Pages stay plain .html — directly editable, no build step — and the compiled
/// result is cached per config version, so a branding save invalidates it immediately.
///
/// Values are HTML-escaped by default. {{raw:token}} opts out, for the handful of copy
/// fields that intentionally carry markup, and {{js:token}} emits a JSON-quoted string
/// literal for use inside a &lt;script&gt; block.
/// </summary>
public sealed partial class PageTemplate
{
    [GeneratedRegex(@"\{\{(raw:|js:)?([a-zA-Z0-9_.]+)\}\}")]
    private static partial Regex TokenPattern();

    private readonly ConfigStore _config;
    private readonly AssetResolver _assets;
    private readonly ConcurrentDictionary<string, (int Version, string Html)> _cache = new();

    public PageTemplate(ConfigStore config, AssetResolver assets)
    {
        _config = config;
        _assets = assets;
    }

    public string Render(string filePath)
    {
        var version = _config.Version;
        if (_cache.TryGetValue(filePath, out var hit) && hit.Version == version) return hit.Html;

        var tokens = BuildTokens(_config.Current);
        var html = TokenPattern().Replace(File.ReadAllText(filePath), match =>
        {
            var mode = match.Groups[1].Value;
            var key = match.Groups[2].Value;
            if (!tokens.TryGetValue(key, out var value)) return match.Value; // leave unknown tokens visible
            return mode switch
            {
                "raw:" => value,
                "js:" => JsonSerializer.Serialize(value),
                _ => WebUtility.HtmlEncode(value)
            };
        });

        _cache[filePath] = (version, html);
        return html;
    }

    public Dictionary<string, string> BuildTokens(ClientConfig c)
    {
        var tokens = ConfigStore.Flatten(c);

        foreach (var name in AssetResolver.Names)
            tokens[$"asset.{name}"] = _assets.Url(name);

        // Convenience aliases so markup reads well.
        tokens["shopName"] = c.Identity.ShopName;
        tokens["shortName"] = c.Identity.ShortName;
        tokens["contactEmail"] = c.Identity.ContactEmail;
        tokens["htmlLang"] = c.Locale.HtmlLang;
        tokens["currencySymbol"] = c.Locale.CurrencySymbol;
        tokens["themeColor"] = c.Theme.BrandPrimary;
        tokens["backgroundColor"] = c.Theme.BrandPrimaryDeep;
        tokens["fontsUrl"] = ThemeCss.FontsUrl(c);
        tokens["configVersion"] = _config.Version.ToString();

        // Derived markup fields.
        tokens["copy.joinHeadlineHtml"] = string.Join("<br>",
            c.Copy.JoinHeadline.Split('\n').Select(line => WebUtility.HtmlEncode(line.Trim())));
        tokens["copy.privacyPolicyHtml"] = Markdown(
            c.Copy.PrivacyPolicy
                .Replace("{{shopName}}", c.Identity.ShopName)
                .Replace("{{contactEmail}}", c.Identity.ContactEmail));

        return tokens;
    }

    /// <summary>
    /// Minimal markdown for the client-editable privacy policy: ### headings, paragraphs,
    /// **bold** and [links](url). Deliberately small — a full parser is a dependency this
    /// does not need, and anything it does not recognise is escaped and shown as text.
    /// </summary>
    public static string Markdown(string source)
    {
        var sb = new StringBuilder();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            sb.Append("<p>").Append(Inline(string.Join(" ", paragraph))).Append("</p>");
            paragraph.Clear();
        }

        // Line-driven rather than block-driven: a heading ends the paragraph above it even
        // when the author did not leave a blank line, which is how people actually write.
        foreach (var rawLine in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) { FlushParagraph(); continue; }

            if (line.StartsWith("### ") || line.StartsWith("## ") || line.StartsWith("# "))
            {
                FlushParagraph();
                sb.Append("<h3>").Append(Inline(line.TrimStart('#').Trim())).Append("</h3>");
            }
            else
            {
                paragraph.Add(line);
            }
        }
        FlushParagraph();
        return sb.ToString();
    }

    private static string Inline(string text)
    {
        var escaped = WebUtility.HtmlEncode(text);
        escaped = LinkPattern().Replace(escaped, m => $"<a href=\"{SafeHref(m.Groups[2].Value)}\">{m.Groups[1].Value}</a>");
        escaped = BoldPattern().Replace(escaped, "<strong>$1</strong>");
        return escaped;
    }

    /// <summary>Only http(s) and mailto survive — blocks javascript: in a client-edited policy.</summary>
    private static string SafeHref(string href) =>
        href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? href
            : "#";

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldPattern();
}
