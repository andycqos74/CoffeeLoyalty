using System.Text;
using CoffeeLoyalty.Config;

namespace CoffeeLoyalty.Branding;

/// <summary>
/// Emits the client's palette as CSS custom properties. Every page links this and uses
/// var(--token) only — no page may contain a literal hex value, which is what makes a
/// re-skin a settings change rather than a code change.
/// </summary>
public static class ThemeCss
{
    public static string Render(ClientConfig config)
    {
        var t = config.Theme;
        var sb = new StringBuilder();
        sb.AppendLine("/* generated from client config — do not edit */");
        sb.AppendLine(":root {");
        Token(sb, "brand-primary", t.BrandPrimary);
        Token(sb, "brand-primary-deep", t.BrandPrimaryDeep);
        Token(sb, "brand-primary-mid", t.BrandPrimaryMid);
        Token(sb, "brand-primary-light", t.BrandPrimaryLight);
        Token(sb, "surface-dark", t.SurfaceDark);
        Token(sb, "card-grad-from", t.CardGradFrom);
        Token(sb, "card-grad-mid", t.CardGradMid);
        Token(sb, "card-grad-to", t.CardGradTo);
        Token(sb, "accent", t.Accent);
        Token(sb, "accent-hover", t.AccentHover);
        Token(sb, "accent-soft", t.AccentSoft);
        Token(sb, "accent-tint", t.AccentTint);
        Token(sb, "on-accent", t.OnAccent);
        Token(sb, "surface", t.Surface);
        Token(sb, "surface-alt", t.SurfaceAlt);
        Token(sb, "accent-ui", t.AccentUi);
        Token(sb, "success", t.Success);
        Token(sb, "success-bright", t.SuccessBright);
        Token(sb, "danger", t.Danger);
        Token(sb, "danger-soft", t.DangerSoft);
        Token(sb, "nav-dark", t.NavDark);
        Token(sb, "modal-surface", t.ModalSurface);
        Token(sb, "install-bar-bg", t.InstallBarBg);
        Token(sb, "font-display", $"'{Sanitize(t.FontDisplay)}', sans-serif");
        Token(sb, "font-body", $"'{Sanitize(t.FontBody)}', sans-serif");

        // Derived translucent shades, so pages never hardcode an rgba() built from the palette.
        Token(sb, "accent-glow", Rgba(t.Accent, 0.40));
        Token(sb, "accent-wash", Rgba(t.Accent, 0.18));
        Token(sb, "accent-edge", Rgba(t.AccentSoft, 0.35));
        Token(sb, "success-wash", Rgba(t.SuccessBright, 0.12));
        Token(sb, "success-edge", Rgba(t.SuccessBright, 0.40));

        // Tinted neutrals. The till and admin chrome was full of blue-tinted greys that
        // would read as wrong on a warm palette, so they are mixed from the brand instead
        // of being fixed values.
        Token(sb, "brand-tint-3", Mix(t.BrandPrimary, 3));
        Token(sb, "brand-tint-8", Mix(t.BrandPrimary, 8));
        Token(sb, "brand-tint-14", Mix(t.BrandPrimary, 14));
        Token(sb, "brand-tint-22", Mix(t.BrandPrimary, 22));
        Token(sb, "nav-fg-dim", $"color-mix(in srgb, #fff 55%, {Sanitize(t.BrandPrimary)})");
        Token(sb, "success-tint", Mix(t.Success, 12));
        Token(sb, "danger-tint", Mix(t.Danger, 10));
        Token(sb, "accent-tint-soft", Mix(t.Accent, 14));
        Token(sb, "accent-ink", $"color-mix(in srgb, {Sanitize(t.Accent)} 70%, #000)");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Google Fonts URL for the client's two families; blank when both are system stacks.</summary>
    public static string FontsUrl(ClientConfig config)
    {
        var families = new[] { config.Theme.FontDisplay, config.Theme.FontBody }
            .Select(f => f?.Trim() ?? "")
            .Where(f => f.Length > 0 && !IsSystemStack(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (families.Length == 0) return "";

        var query = string.Join("&", families.Select(f =>
            $"family={Uri.EscapeDataString(f).Replace("%20", "+")}:wght@400;500;600;700;800"));
        return $"https://fonts.googleapis.com/css2?{query}&display=swap";
    }

    private static bool IsSystemStack(string family) =>
        family.Equals("system-ui", StringComparison.OrdinalIgnoreCase)
        || family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Brand colour mixed into white, for tinted backgrounds that must track the palette.</summary>
    private static string Mix(string colour, int percent) =>
        $"color-mix(in srgb, {Sanitize(colour)} {percent}%, #fff)";

    private static void Token(StringBuilder sb, string name, string value) =>
        sb.AppendLine($"  --{name}: {Sanitize(value)};");

    /// <summary>Config is admin-supplied; strip anything that could close the declaration.</summary>
    private static string Sanitize(string value) =>
        new(value.Where(ch => ch is not (';' or '{' or '}' or '<' or '>' or '\\' or '\'' or '"')).ToArray());

    /// <summary>#rrggbb -> rgba(r,g,b,a). Falls back to the raw value if it isn't a hex colour.</summary>
    public static string Rgba(string hex, double alpha)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out _))
            return Sanitize(hex);

        var r = Convert.ToInt32(h[..2], 16);
        var g = Convert.ToInt32(h.Substring(2, 2), 16);
        var b = Convert.ToInt32(h.Substring(4, 2), 16);
        return $"rgba({r},{g},{b},{alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
    }

    /// <summary>Relative luminance per WCAG, used by the admin UI's contrast warnings.</summary>
    public static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out _)) return 0;

        double Channel(string part)
        {
            var v = Convert.ToInt32(part, 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(h[..2]) + 0.7152 * Channel(h.Substring(2, 2)) + 0.0722 * Channel(h.Substring(4, 2));
    }

    public static double ContrastRatio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }
}
