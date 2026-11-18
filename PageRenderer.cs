// Fills a customer page template (templates/*.html) with the theme and the page's current words and photos.
//
//   {{key}}                the field's formatted HTML (see PageContent formatting)
//   {{text:key}}           the field as plain escaped text (titles, attributes)
//   {{src:key}}            a photo field's address
//   {{img:key|class}}      the photo as <img>, wrapped in <animated-image> when it has an animation; empty if unset
//   {{json:prefix}}        a JSON object of the formatted fields under prefix (for page scripts), e.g. {{json:card}}
//   {{setting:shop_name}}  a setting (escaped)
//   {{theme:fontsLink}} {{theme:themeColor}} {{theme:stampIcon}} {{theme:rewardIcon}} {{theme:name}}
//   {{engine:head}}        engine extras for <head>: the animation player when needed, the preview flag

using System.Text.Json;
using System.Text.RegularExpressions;

// An animated photo: its analysis id (served at /anim/{id}.json) and its effect config JSON.
public sealed record AnimationRef(long Id, string ConfigJson);

public static class PageRenderer
{
    static readonly Regex Placeholder = new(@"\{\{(?:(text|img|src|json|setting|theme|engine):)?([\w.]+)(?:\|([\w -]+))?\}\}", RegexOptions.Compiled);

    public static string Render(string template, Theme theme, IReadOnlyDictionary<string, string> values,
        Func<string, string> setting, Func<string, AnimationRef?> animation, bool preview)
    {
        var usesAnimation = false;
        string Esc(string? s) => PageContent.Esc(s);

        var html = Placeholder.Replace(template, m =>
        {
            var kind = m.Groups[1].Value;
            var key = m.Groups[2].Value;
            var arg = m.Groups[3].Success ? m.Groups[3].Value : null;
            if (kind == "engine") return m.Value; // filled in last, once we know what the page uses
            if (kind == "setting") return Esc(setting(key));
            if (kind == "theme") return key switch
            {
                "fontsLink" => string.IsNullOrEmpty(theme.FontsUrl) ? "" :
                    $"<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\"><link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin><link rel=\"stylesheet\" href=\"{Esc(theme.FontsUrl)}\">",
                "themeColor" => Esc(theme.Colours.ThemeColor),
                "stampIcon" => theme.StampIcon,
                "rewardIcon" => theme.RewardIcon,
                "name" => Esc(theme.Name),
                _ => ""
            };
            if (kind == "json")
            {
                var map = PageContent.Fields.Where(f => f.Key.StartsWith(key + "."))
                    .ToDictionary(f => f.Key[(key.Length + 1)..], f => PageContent.Format(f, values.GetValueOrDefault(f.Key, "")));
                return JsonSerializer.Serialize(map); // the default encoder escapes < > & so this is safe inside <script>
            }

            var field = PageContent.Field(key);
            if (field is null) return "";
            var v = values.GetValueOrDefault(key, "");
            switch (kind)
            {
                case "text": return Esc(v.Replace("\n", " "));
                case "src": return Esc(v);
                case "img":
                {
                    if (string.IsNullOrEmpty(v)) return "";
                    var cls = arg is null ? "" : $" class=\"{Esc(arg)}\"";
                    var anim = animation(v);
                    if (anim is null) return $"<img src=\"{Esc(v)}\" alt=\"\"{cls}>";
                    usesAnimation = true;
                    var controls = anim.ConfigJson.Contains("\"pauseButton\":false") ? "" : " controls";
                    return $"<animated-image{cls} src=\"/anim/{anim.Id}.json\" config=\"{Esc(anim.ConfigJson)}\"{controls}><img src=\"{Esc(v)}\" alt=\"\"></animated-image>";
                }
                default: return PageContent.Format(field, v);
            }
        });

        var head = usesAnimation ? "<script src=\"/lib/animated-image.js\" defer></script>" : "";
        if (preview) head += "<script>window.__PREVIEW = true;</script><style>.install-bar{display:none!important}</style>";
        return html.Replace("{{engine:head}}", head);
    }
}
