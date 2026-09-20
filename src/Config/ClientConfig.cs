using System.Text.Json.Serialization;

namespace CoffeeLoyalty.Config;

/// <summary>
/// Everything that differs between establishments. Defaults below are the current
/// Queen of the South Café values, so a config-less install renders exactly as it does today.
///
/// Resolution order (see ConfigStore): these defaults -> client.json (provisioning seed)
/// -> DB settings overrides written by the admin Branding tab.
/// </summary>
public sealed record ClientConfig
{
    public IdentityConfig Identity { get; init; } = new();
    public LocaleConfig Locale { get; init; } = new();
    public ProgrammeConfig Programme { get; init; } = new();
    public ThemeConfig Theme { get; init; } = new();
    public CopyConfig Copy { get; init; } = new();
    public WalletConfig Wallet { get; init; } = new();
}

public sealed record IdentityConfig
{
    /// <summary>URL-safe, immutable. Names the config dir and scopes the Wallet class for new clients.</summary>
    public string Slug { get; init; } = "qosfc";
    public string ShopName { get; init; } = "Queen of the South Café";
    /// <summary>Short form for tight spaces (page headers, brand line).</summary>
    public string ShortName { get; init; } = "Queen of the South";
    public string ContactEmail { get; init; } = "hello@queensofthecafe.co.uk";

    /// <summary>
    /// Canonical absolute origin, e.g. "https://qos.myloyalty.com". Set this whenever a
    /// client is reachable on more than one hostname: Google Wallet stores the programme
    /// logo URL on its own servers, so letting it follow whichever host happened to make
    /// the last request makes the pass design flap between domains. Blank falls back to
    /// the request's own scheme and host.
    /// </summary>
    public string PublicBaseUrl { get; init; } = "";
}

public sealed record LocaleConfig
{
    public string HtmlLang { get; init; } = "en";
    /// <summary>BCP-47 tag used for Google Wallet localised strings and date formatting.</summary>
    public string Language { get; init; } = "en-GB";
    /// <summary>IANA zone. Reporting converts stored UTC into this before bucketing by day.</summary>
    public string TimeZone { get; init; } = "Europe/London";
    public string CurrencyCode { get; init; } = "GBP";
    public string CurrencySymbol { get; init; } = "£";
    /// <summary>Digits after the decimal point. 2 for GBP/EUR/USD, 0 for JPY.</summary>
    public int CurrencyMinorDigits { get; init; } = 2;
    /// <summary>"monday" or "sunday" — controls dashboard week bucketing.</summary>
    public string WeekStart { get; init; } = "monday";
    public string DateFormat { get; init; } = "ddd d MMM";
}

public sealed record ProgrammeConfig
{
    /// <summary>Points earned per whole currency unit spent (legacy key: points_per_pound).</summary>
    public int PointsPerUnit { get; init; } = 10;
    /// <summary>Till quick-amount buttons, in whole currency units.</summary>
    public int[] QuickSpend { get; init; } = { 5, 10, 20 };
    public int[] QuickStamp { get; init; } = { 1, 2, 3, 4, 5 };
    /// <summary>Largest single transaction, in minor units.</summary>
    public long MaxTransactionMinor { get; init; } = 100_000;
    /// <summary>Card stamp-grid glyph: cup | trophy | star | ball | heart.</summary>
    public string StampIcon { get; init; } = "cup";
    /// <summary>Filled/empty glyphs for the Google Wallet pass, which cannot render SVG.</summary>
    public string StampEmojiFilled { get; init; } = "🏆";
    public string StampEmojiEmpty { get; init; } = "○";
    /// <summary>Fallback noun when a stamp reward has no item_name.</summary>
    public string DefaultItemNoun { get; init; } = "item";
}

/// <summary>
/// Every colour the four pages use. Emitted as CSS custom properties by /theme.css;
/// no page may contain a literal hex value.
/// </summary>
public sealed record ThemeConfig
{
    public string BrandPrimary { get; init; } = "#094582";
    public string BrandPrimaryDeep { get; init; } = "#052038";
    public string BrandPrimaryMid { get; init; } = "#08315b";
    public string BrandPrimaryLight { get; init; } = "#0d5396";
    public string SurfaceDark { get; init; } = "#061d33";
    public string CardGradFrom { get; init; } = "#12588f";
    public string CardGradMid { get; init; } = "#0a3e6c";
    public string CardGradTo { get; init; } = "#062b4e";
    public string Accent { get; init; } = "#009fff";
    public string AccentHover { get; init; } = "#33b3ff";
    public string AccentSoft { get; init; } = "#5fc4ff";
    public string AccentTint { get; init; } = "#7fd0ff";
    public string OnAccent { get; init; } = "#04243f";
    public string Surface { get; init; } = "#f0f4fa";
    public string SurfaceAlt { get; init; } = "#eaf1fb";
    public string AccentUi { get; init; } = "#1a6abf";
    public string Success { get; init; } = "#2e8b57";
    public string SuccessBright { get; init; } = "#34d399";
    public string Danger { get; init; } = "#c0392b";
    public string DangerSoft { get; init; } = "#ff8fa3";
    public string NavDark { get; init; } = "#062f5a";
    public string ModalSurface { get; init; } = "#0d2d4a";
    public string InstallBarBg { get; init; } = "#0a2e50";
    public string FontDisplay { get; init; } = "Space Grotesk";
    public string FontBody { get; init; } = "Manrope";
}

public sealed record CopyConfig
{
    // Join page
    public string JoinTitle { get; init; } = "Join — Queen of the South Café";
    public string JoinPill { get; init; } = "Loyalty club";
    /// <summary>Rendered above the accent word; newlines become line breaks.</summary>
    public string JoinHeadline { get; init; } = "Coffee\nfit for";
    public string JoinHeadlineAccent { get; init; } = "royalty.";
    public string JoinSubcopy { get; init; } =
        "Collect a stamp with every cup. Fill your card and the next one's free — no app, no fuss.";
    public string JoinNameLabel { get; init; } = "Your name";
    public string JoinNamePlaceholder { get; init; } = "e.g. Alex Morgan";
    public string JoinEmailLabel { get; init; } = "Email";
    public string JoinCta { get; init; } = "Get my loyalty card";
    public string JoinAlready { get; init; } = "Already joined? Open my card";
    public string ConsentText { get; init; } =
        "I'd like to receive news and special offers by email from Queen of the South Café.";
    /// <summary>Markdown. Seeded from a UK-GDPR template; fully client-editable.</summary>
    public string PrivacyPolicy { get; init; } = DefaultPrivacyPolicy;

    // Card page
    public string CardTitle { get; init; } = "My QOSFC Loyalty Card";
    public string CardWelcome { get; init; } = "Welcome back";
    public string CardQrLabel { get; init; } = "Scan to earn & redeem";
    public string CardActivityTitle { get; init; } = "Recent activity";
    public string CardActivityEmpty { get; init; } = "No activity yet — show your QR at the till!";
    public string CardRewardsTitle { get; init; } = "Points rewards";
    public string CardTip { get; init; } = "Tip: use <b>Add to Home Screen</b> to keep your card one tap away.";
    public string InstallTitle { get; init; } = "Add to Home Screen";
    public string InstallSubCard { get; init; } = "Keep your card one tap away";
    public string InstallSubJoin { get; init; } = "Install for quick access";
    public string InstallSubIos { get; init; } = "Tap Share → Add to Home Screen";

    // Till + admin
    public string ShopTitle { get; init; } = "Till — QOSFC Loyalty";
    public string ShopHeading { get; init; } = "Till";
    public string ShopScanHint { get; init; } = "Point camera at the customer's QR code";
    public string ShopCustomAmount { get; init; } = "Custom £";
    public string AdminTitle { get; init; } = "Queen of the South Café — Admin";
    public string AdminHeading { get; init; } = "Queen of the South Café — Admin";

    // PWA
    public string PwaName { get; init; } = "Queen of the South Café Loyalty";
    public string PwaShortName { get; init; } = "QOSFC Loyalty";
    public string PwaDescription { get; init; } = "Collect stamps and earn rewards at Queen of the South Café.";
    public string PwaCardName { get; init; } = "QOSFC Loyalty Card";
    public string PwaCardShortName { get; init; } = "My Card";
    public string PwaTillName { get; init; } = "QOSFC Loyalty Till";
    public string PwaTillShortName { get; init; } = "Till";

    [JsonIgnore]
    public const string DefaultPrivacyPolicy = """
        ### Who we are
        {{shopName}} is the data controller for information collected through this loyalty programme.

        ### What we collect
        We collect your name, email address (optional), and a record of stamps and rewards earned through our loyalty scheme.

        ### How we use it
        Your information is used solely to operate your loyalty card — tracking stamps, notifying you of rewards, and letting you access your card on any device. If you opt in, we may also send you news and special offers by email.

        ### Marketing emails
        We will only send marketing emails if you tick the opt-in box when signing up. You can withdraw consent at any time by contacting us at the address below.

        ### How long we keep it
        We retain your data for as long as your loyalty account is active. You can request deletion at any time.

        ### Your rights
        Under UK GDPR you have the right to access, correct, or delete your personal data, and to object to or restrict how we use it. To exercise any of these rights, or to withdraw marketing consent, please contact us at [{{contactEmail}}](mailto:{{contactEmail}}).

        ### Sharing
        We do not sell or share your personal data with third parties for their own marketing purposes.
        """;
}

public sealed record WalletConfig
{
    /// <summary>Blank derives "{shopName} Loyalty" — a per-client default must never be
    /// another client's programme name.</summary>
    public string ProgramName { get; init; } = "";
    /// <summary>
    /// Google Wallet class is {issuerId}.{classSuffix}. Clients sharing one platform issuer MUST
    /// have distinct suffixes or they overwrite each other's pass design on Google's servers.
    /// Existing clients stay pinned to "loyalty_card" so already-saved passes keep updating.
    /// </summary>
    public string ClassSuffix { get; init; } = "loyalty_card";
    /// <summary>Object id is {issuerId}.{objectPrefix}{token}. Changing this orphans saved passes.</summary>
    public string ObjectPrefix { get; init; } = "loyalty_";
    /// <summary>Pass background. Falls back to Theme.BrandPrimary when blank.</summary>
    public string BackgroundColor { get; init; } = "";
}
