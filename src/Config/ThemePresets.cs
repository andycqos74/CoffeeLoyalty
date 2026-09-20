namespace CoffeeLoyalty.Config;

/// <summary>
/// Vetted palettes a new establishment can start from in one click, then override
/// individual tokens. Each is checked for readable text-on-background contrast, so a
/// client who never opens the colour pickers still gets a presentable site.
/// </summary>
public static class ThemePresets
{
    public sealed record Preset(string Id, string Name, string Description, ThemeConfig Theme);

    public static readonly Preset[] All =
    {
        new("deepBlue", "Deep Blue", "Cool navy with a bright cyan accent — the original palette.",
            new ThemeConfig()),

        new("warmBrown", "Warm Brown", "Espresso browns with a caramel accent. Classic coffee house.",
            new ThemeConfig
            {
                BrandPrimary = "#4b2e2b", BrandPrimaryDeep = "#1c0f0e", BrandPrimaryMid = "#3a2220",
                BrandPrimaryLight = "#6b4340", SurfaceDark = "#1a0e0d",
                CardGradFrom = "#7a4c47", CardGradMid = "#5a3633", CardGradTo = "#3a2220",
                Accent = "#e8a33d", AccentHover = "#f2b95f", AccentSoft = "#f5c77e",
                AccentTint = "#ffdca8", OnAccent = "#2a1715",
                Surface = "#faf5f0", SurfaceAlt = "#f2e6da", AccentUi = "#a9663c",
                NavDark = "#2a1715", ModalSurface = "#3a2220", InstallBarBg = "#2f1c1a"
            }),

        new("forest", "Forest", "Deep greens with a fresh mint accent. Good for delis and grocers.",
            new ThemeConfig
            {
                BrandPrimary = "#1d4d3a", BrandPrimaryDeep = "#07231a", BrandPrimaryMid = "#133c2d",
                BrandPrimaryLight = "#2a6b50", SurfaceDark = "#062017",
                CardGradFrom = "#2b7d5c", CardGradMid = "#1d5a42", CardGradTo = "#123c2c",
                Accent = "#4ade80", AccentHover = "#6ee7a3", AccentSoft = "#86efac",
                AccentTint = "#bbf7d0", OnAccent = "#052e1c",
                Surface = "#f1f8f4", SurfaceAlt = "#dcf0e5", AccentUi = "#2f8f63",
                NavDark = "#0d3527", ModalSurface = "#133c2d", InstallBarBg = "#0f3829"
            }),

        new("charcoal", "Charcoal", "Neutral greys with an amber accent. Understated and modern.",
            new ThemeConfig
            {
                BrandPrimary = "#2b2f36", BrandPrimaryDeep = "#101317", BrandPrimaryMid = "#1d2126",
                BrandPrimaryLight = "#3b414a", SurfaceDark = "#0e1114",
                CardGradFrom = "#454c57", CardGradMid = "#31373f", CardGradTo = "#20252b",
                Accent = "#f59e0b", AccentHover = "#fbbf24", AccentSoft = "#fcd34d",
                AccentTint = "#fde68a", OnAccent = "#1a1206",
                Surface = "#f5f6f8", SurfaceAlt = "#e6e9ee", AccentUi = "#6b7280",
                NavDark = "#1a1d22", ModalSurface = "#1d2126", InstallBarBg = "#1a1e23"
            }),

        new("blush", "Blush", "Deep plum with a soft pink accent. Bakeries and patisseries.",
            new ThemeConfig
            {
                BrandPrimary = "#7c2d4a", BrandPrimaryDeep = "#2d0f1c", BrandPrimaryMid = "#5a2036",
                BrandPrimaryLight = "#9c3f60", SurfaceDark = "#280d19",
                CardGradFrom = "#a34a6b", CardGradMid = "#7c2d4a", CardGradTo = "#5a2036",
                Accent = "#f472b6", AccentHover = "#f9a8d4", AccentSoft = "#fbcfe8",
                AccentTint = "#fce7f3", OnAccent = "#3d0f22",
                Surface = "#fdf4f7", SurfaceAlt = "#fae3ec", AccentUi = "#b3527a",
                NavDark = "#3d1526", ModalSurface = "#5a2036", InstallBarBg = "#38121f"
            })
    };

    public static Preset? Find(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
