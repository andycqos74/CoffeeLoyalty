// Animated photos, ported from the Gleam site engine (src/anim): any photo on the customer pages (an upload in
// /media or a theme photo) can be given a subtle looping animation from Admin -> Pages. The admin's browser
// analyses the photo for points of light (wwwroot/admin/anim-analyse.js, the same algorithm as Gleam's
// analyse.js), the admin picks a preset and adjusts it, or describes what they want and Claude proposes settings,
// and visitors' browsers play it with <animated-image> (wwwroot/lib/animated-image.js) on a canvas.
//
// Parallax is left out for now: the presets and Claude only use drift, twinkle, sparkle, pulses and particles.
// Claude only ever proposes: its settings load into the admin's preview and reach visitors when "Save animation"
// is pressed.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

public sealed class AnimationError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public static class AnimationConfig
{
    public static readonly string[] EffectTypes = ["drift", "twinkle", "sparkle", "pulses", "particles"];
    public static readonly string[] ParticleKinds = ["bubbles", "dust", "snow", "embers"];

    public static readonly Dictionary<string, string> EffectLabels = new()
    {
        ["drift"] = "Slow drift", ["twinkle"] = "Twinkling lights", ["sparkle"] = "Light sparkle",
        ["pulses"] = "Pulses along lines", ["particles"] = "Particles",
    };

    public static JsonObject EffectDefault(string type) => type switch
    {
        "drift" => new JsonObject { ["type"] = "drift", ["zoom"] = 0.025, ["panX"] = 14, ["panY"] = 8 },
        "twinkle" => new JsonObject { ["type"] = "twinkle", ["source"] = "stars", ["density"] = 0.5 },
        "sparkle" => new JsonObject { ["type"] = "sparkle", ["source"] = "nodes", ["flare"] = true },
        "pulses" => new JsonObject { ["type"] = "pulses", ["from"] = "hub", ["to"] = "nodes", ["minArea"] = 40 },
        "particles" => new JsonObject { ["type"] = "particles", ["kind"] = "dust", ["count"] = 24, ["size"] = 1 },
        _ => throw new ArgumentException(type)
    };

    static (string label, JsonObject config) Preset(string label, string[] types, double intensity, double speed) =>
        (label, new JsonObject
        {
            ["version"] = 1, ["loopSeconds"] = 8, ["speed"] = speed, ["intensity"] = intensity, ["pauseButton"] = true,
            ["effects"] = new JsonArray(types.Select(t => (JsonNode)EffectDefault(t)).ToArray())
        });

    // Gleam's presets without parallax.
    public static readonly (string key, string label, string[] types, double intensity, double speed)[] PresetList =
    [
        ("subject", "Gentle drift", ["drift"], 0.6, 0.8),
        ("subtle", "Subtle", ["drift", "twinkle", "sparkle"], 0.45, 0.8),
        ("network", "Network alive", ["drift", "twinkle", "sparkle", "pulses"], 0.7, 1),
        ("twinkle", "Twinkle only", ["twinkle", "sparkle"], 0.65, 1),
        ("cinematic", "Cinematic drift", ["drift", "twinkle"], 0.4, 0.6),
    ];

    public static JsonObject Presets() => new(PresetList.Select(p =>
        KeyValuePair.Create(p.key, (JsonNode?)new JsonObject { ["label"] = p.label, ["config"] = Preset(p.label, p.types, p.intensity, p.speed).config })));

    // The preset to start from for a freshly analysed photo: lights on a dark picture get the light effects;
    // anything else (people, a room, a cup of coffee) gets the gentle drift.
    public static string SuggestPreset(JsonElement analysis)
    {
        var nodes = analysis.TryGetProperty("lights", out var l) && l.TryGetProperty("nodes", out var n) ? n.GetArrayLength() : 0;
        var dark = analysis.TryGetProperty("brightness", out var b) && b.GetDouble() < 0.35;
        if (dark && nodes >= 8) return "network";
        if (dark && nodes >= 3) return "subtle";
        return "subject";
    }

    static double Num(JsonNode? v, double min, double max, double fallback)
    {
        double d;
        try { d = v?.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : fallback; } catch { d = fallback; }
        if (double.IsNaN(d) || double.IsInfinity(d)) d = fallback;
        return Math.Min(max, Math.Max(min, d));
    }
    static bool Bool(JsonNode? v, bool fallback) => v?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : fallback;
    static double R(double d, int places = 3) => Math.Round(d, places);

    // Clamps a particles region to the picture: { bbox: [x, y, w, h] } or { poly: [[x, y], ...] }, or a bare bbox
    // array (as Claude returns it). Without one, the middle third of the picture.
    static JsonObject Region(JsonNode? region, int W, int H)
    {
        if (region is JsonObject ro && ro["poly"] is JsonArray poly && poly.Count is >= 3 and <= 32 && poly.All(p => p is JsonArray { Count: 2 }))
            return new JsonObject { ["poly"] = new JsonArray(poly.Select(p => (JsonNode)new JsonArray(Math.Round(Num(p![0], 0, W, 0)), Math.Round(Num(p[1], 0, H, 0)))).ToArray()) };
        var b = region as JsonArray ?? (region as JsonObject)?["bbox"] as JsonArray;
        if (b is { Count: 4 } && b.All(v => v?.GetValueKind() == JsonValueKind.Number))
        {
            var x = Math.Round(Num(b[0], 0, W - 8, 0)); var y = Math.Round(Num(b[1], 0, H - 8, 0));
            return new JsonObject { ["bbox"] = new JsonArray(x, y, Math.Round(Num(b[2], 8, W - x, W - x)), Math.Round(Num(b[3], 8, H - y, H - y))) };
        }
        return new JsonObject { ["bbox"] = new JsonArray(W / 3, H / 3, W / 3, H / 3) };
    }

    // Returns a clean config or throws. W/H: the analysed picture's size, for pixel values.
    public static JsonObject Clean(JsonNode? input, int W, int H)
    {
        if (input is not JsonObject o) throw new AnimationError("The animation settings are missing.");
        var effects = o["effects"] as JsonArray ?? [];
        if (effects.Count > 10) throw new AnimationError("Too many effects (10 at most).");
        var seen = new HashSet<string>();
        var outList = new JsonArray();
        foreach (var e in effects)
        {
            var type = e?["type"]?.GetValueKind() == JsonValueKind.String ? e["type"]!.GetValue<string>() : "";
            if (type == "parallax") continue; // not offered yet
            if (!EffectTypes.Contains(type)) throw new AnimationError($"Unknown animation effect: {type[..Math.Min(type.Length, 40)]}");
            if (type != "particles" && !seen.Add(type)) continue;
            var c = new JsonObject { ["type"] = type };
            switch (type)
            {
                case "drift":
                    var maxPan = Math.Round(W * 0.015);
                    c["zoom"] = R(Num(e!["zoom"], 0, 0.05, 0.025));
                    c["panX"] = Math.Round(Num(e["panX"], -maxPan, maxPan, 14));
                    c["panY"] = Math.Round(Num(e["panY"], -maxPan, maxPan, 8));
                    break;
                case "twinkle": c["source"] = "stars"; c["density"] = R(Num(e!["density"], 0, 1, 0.5)); break;
                case "sparkle": c["source"] = "nodes"; c["flare"] = Bool(e!["flare"], true); break;
                case "pulses": c["from"] = "hub"; c["to"] = "nodes"; c["minArea"] = Math.Round(Num(e!["minArea"], 0, 10000, 40)); break;
                case "particles":
                    if (outList.Count(x => x?["type"]?.GetValue<string>() == "particles") >= 3) continue;
                    var kind = e!["kind"]?.GetValueKind() == JsonValueKind.String ? e["kind"]!.GetValue<string>() : "dust";
                    c["kind"] = ParticleKinds.Contains(kind) ? kind : "dust";
                    c["region"] = Region(e["region"] ?? e["bbox"], W, H);
                    c["count"] = Math.Round(Num(e["count"], 1, 60, 24));
                    c["size"] = R(Num(e["size"], 0.3, 3, 1));
                    break;
            }
            outList.Add(c);
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["loopSeconds"] = Math.Round(Num(o["loopSeconds"], 4, 12, 8)),
            ["speed"] = R(Num(o["speed"], 0.25, 2, 1), 2),
            ["intensity"] = R(Num(o["intensity"], 0, 1, 0.6), 2),
            ["pauseButton"] = Bool(o["pauseButton"], true),
            ["effects"] = outList,
        };
    }

    // Validates the analysis the admin's browser sends: { width, height, brightness, lights: { nodes, stars, hub } }.
    public static JsonObject CleanAnalysis(JsonNode? input, string src)
    {
        if (input is not JsonObject a) throw new AnimationError("The photo analysis is missing.");
        var w = (int)Num(a["width"], 16, 4096, 0); var h = (int)Num(a["height"], 16, 4096, 0);
        if (w < 16 || h < 16) throw new AnimationError("The photo analysis is incomplete.");
        JsonArray Points(JsonNode? list, int max, int maxThird) =>
            new((list as JsonArray ?? []).Take(max).Where(p => p is JsonArray { Count: 3 })
                .Select(p => (JsonNode)new JsonArray(Math.Round(Num(p![0], 0, w, 0)), Math.Round(Num(p[1], 0, h, 0)), Math.Round(Num(p[2], 0, maxThird, 0)))).ToArray());
        var lights = a["lights"] as JsonObject ?? new JsonObject();
        var nodes = Points(lights["nodes"], 80, w * h);
        var stars = Points(lights["stars"], 700, 255);
        return new JsonObject
        {
            ["version"] = 1, ["width"] = w, ["height"] = h,
            ["brightness"] = R(Num(a["brightness"], 0, 1, 0.5)),
            // One flat layer (the photo itself) - parallax layers are not part of this version.
            ["layers"] = new JsonArray(new JsonObject { ["src"] = src, ["depth"] = 0.45 }),
            ["lights"] = new JsonObject
            {
                ["nodes"] = nodes, ["stars"] = stars,
                ["hub"] = nodes.Count > 0 ? new JsonArray(nodes[0]![0]!.GetValue<double>(), nodes[0]![1]!.GetValue<double>()) : null,
            },
        };
    }
}

// Per-photo storage (animations table), lookup for the page renderer, and the Claude call.
public sealed class Animations(Func<SqliteConnection> open, IConfiguration config, ILogger logger)
{
    public const int MaxPrompt = 1000;

    public void InitTable(SqliteConnection c)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS animations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                src TEXT NOT NULL UNIQUE,
                analysis TEXT NOT NULL,
                config TEXT,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string ApiKey => (config["ANTHROPIC_API_KEY"] ?? "").Trim();
    public bool AiAvailable => ApiKey != "";
    string Model => string.IsNullOrWhiteSpace(config["ANIMATE_MODEL"]) ? "claude-opus-5-5" : config["ANIMATE_MODEL"]!.Trim();
    string EffortName => config["ANIMATE_EFFORT"] is "low" or "medium" or "high" or "xhigh" or "max" ? config["ANIMATE_EFFORT"]! : "medium";

    // src -> what plays there. Small table; read per page view.
    public AnimationRef? Lookup(SqliteConnection c, string src)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, config FROM animations WHERE src = $s AND config IS NOT NULL";
        cmd.Parameters.AddWithValue("$s", src);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new AnimationRef(r.GetInt64(0), r.GetString(1)) : null;
    }

    (long id, JsonObject analysis, JsonObject? config)? Row(SqliteConnection c, string src)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, analysis, config FROM animations WHERE src = $s";
        cmd.Parameters.AddWithValue("$s", src);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt64(0), JsonNode.Parse(r.GetString(1))!.AsObject(), r.IsDBNull(2) ? null : JsonNode.Parse(r.GetString(2))!.AsObject());
    }

    public static void CheckSrc(string src)
    {
        if (!PageContent.IsImageUrl(src) || src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || src.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            throw new AnimationError("Only photos (JPEG, PNG or WebP) uploaded here or included with the theme can be animated.");
    }

    // What the admin's Animate panel needs. needsAnalysis = the browser must analyse the photo first.
    public JsonObject Panel(string src)
    {
        CheckSrc(src);
        using var c = open();
        var row = Row(c, src);
        var info = new JsonObject
        {
            ["src"] = src,
            ["presets"] = AnimationConfig.Presets(),
            ["effects"] = new JsonArray(AnimationConfig.EffectTypes.Select(t => (JsonNode)new JsonObject
                { ["type"] = t, ["label"] = AnimationConfig.EffectLabels[t], ["defaults"] = AnimationConfig.EffectDefault(t) }).ToArray()),
            ["particleKinds"] = new JsonArray(AnimationConfig.ParticleKinds.Select(k => (JsonNode)k).ToArray()),
            ["ai"] = AiAvailable,
            ["needsAnalysis"] = row is null,
        };
        if (row is { } r)
        {
            var a = r.analysis;
            var lights = a["lights"]!.AsObject();
            info["analysisUrl"] = $"/anim/{r.id}.json";
            info["width"] = a["width"]!.DeepClone();
            info["height"] = a["height"]!.DeepClone();
            info["found"] = new JsonObject { ["nodes"] = lights["nodes"]!.AsArray().Count, ["stars"] = lights["stars"]!.AsArray().Count, ["hub"] = lights["hub"] is not null };
            info["config"] = r.config?.DeepClone();
            info["saved"] = r.config is not null;
            info["suggested"] = AnimationConfig.SuggestPreset(JsonDocument.Parse(a.ToJsonString()).RootElement);
        }
        return info;
    }

    public JsonObject StoreAnalysis(string src, JsonNode? analysis)
    {
        CheckSrc(src);
        var clean = AnimationConfig.CleanAnalysis(analysis, src);
        using (var c = open())
        {
            var cmd = c.CreateCommand();
            // Re-analysing replaces the light positions and keeps the saved settings.
            cmd.CommandText = "INSERT INTO animations (src, analysis) VALUES ($s, $a) ON CONFLICT(src) DO UPDATE SET analysis = $a, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("$s", src);
            cmd.Parameters.AddWithValue("$a", clean.ToJsonString());
            cmd.ExecuteNonQuery();
        }
        return Panel(src);
    }

    public JsonObject? Save(string src, JsonNode? input)
    {
        CheckSrc(src);
        using var c = open();
        var row = Row(c, src) ?? throw new AnimationError("Analyse the photo first.");
        var clean = input is null ? null : AnimationConfig.Clean(input, row.analysis["width"]!.GetValue<int>(), row.analysis["height"]!.GetValue<int>());
        var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE animations SET config = $c, updated_at = datetime('now') WHERE src = $s";
        cmd.Parameters.AddWithValue("$s", src);
        cmd.Parameters.AddWithValue("$c", (object?)clean?.ToJsonString() ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return clean;
    }

    // The analysis file the player loads.
    public string? AnalysisFile(long id)
    {
        using var c = open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT analysis FROM animations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var s = cmd.ExecuteScalar() as string;
        if (s is null) return null;
        var a = JsonNode.Parse(s)!.AsObject();
        return new JsonObject { ["version"] = 1, ["width"] = a["width"]!.DeepClone(), ["height"] = a["height"]!.DeepClone(), ["layers"] = a["layers"]!.DeepClone(), ["lights"] = a["lights"]!.DeepClone() }.ToJsonString();
    }

    // ---------------------------------------------------------------- Claude: description -> settings

    public const string SystemPrompt = """
        You set up subtle looping animations for still photos on a loyalty-card website (a café or restaurant's join
        page and member card). The editor describes what they want; you return animation settings that follow the
        given JSON schema exactly.

        How the animation works: the still image is drawn on a canvas and effects play on top of it, in a seamless loop.
        - drift: the whole picture slowly zooms and pans.
        - twinkle: the small bright star-like points found in the image twinkle.
        - sparkle: the larger bright lights found in the image glow and flare.
        - pulses: dots of light travel from the brightest light (the hub) along straight lines to the other lights.
        - particles: bubbles, dust, snow or embers drift inside one rectangular region (embers rise, like steam or
          sparks; dust floats; snow falls; bubbles rise).

        Rules:
        - Choose only from these effect types. Use twinkle, sparkle and pulses only when the analysis found lights
          (sparkle needs nodes; pulses needs a hub and several nodes; twinkle needs stars).
        - Keep motion subtle by default: this sits behind text on a web page. Only go strong when asked.
        - For particles, base the region on the image and the analysis data. You can't see exact pixel positions: give
          your best approximate bbox in image pixels and set needsReview to true.
        - If the request can't be done with these effects (for example making a person wave, parallax or adding
          sound), choose the closest settings and say so in note.
        - The note is for the editor: one or two short, plain sentences in British English.
        """;

    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    static object Effect(string type, Dictionary<string, object> props)
    {
        var properties = new Dictionary<string, object> { ["type"] = new { type = "string", @const = type } };
        foreach (var (k, v) in props) properties[k] = v;
        return new { type = "object", additionalProperties = false, required = properties.Keys.ToArray(), properties };
    }
    static object N(string description) => new { type = "number", description };

    // The JSON Schema Claude's answer must follow (structured outputs). Ranges are described in words and Clean()
    // clamps them afterwards.
    public static readonly Dictionary<string, JsonElement> ModelSchema = new()
    {
        ["type"] = J("object"),
        ["additionalProperties"] = J(false),
        ["required"] = J(new[] { "intensity", "speed", "loopSeconds", "effects", "note", "needsReview" }),
        ["properties"] = J(new Dictionary<string, object>
        {
            ["intensity"] = N("Overall strength of the light effects and motion, 0 to 1. Default 0.5; subtle is 0.3 to 0.6."),
            ["speed"] = N("Playback speed, 0.25 to 2. 1 is normal; 0.6 to 0.8 feels calm."),
            ["loopSeconds"] = new { type = "integer", description = "Length of one seamless loop in seconds, 4 to 12. Usually 8." },
            ["effects"] = new
            {
                type = "array",
                description = "The effects to use, at most one of each type except particles (up to 3 regions).",
                items = new
                {
                    anyOf = new[]
                    {
                        Effect("drift", new() { ["zoom"] = N("Slow zoom amount, 0 to 0.05 (0.025 typical)."), ["panX"] = N("Horizontal drift in image pixels, small (under 1.5% of the width)."), ["panY"] = N("Vertical drift in image pixels, small.") }),
                        Effect("twinkle", new() { ["density"] = N("Share of the small star-like points that twinkle, 0 to 1.") }),
                        Effect("sparkle", new() { ["flare"] = new { type = "boolean", description = "Add a four-point star flare at the peak of each light." } }),
                        Effect("pulses", new() { ["minArea"] = N("Only send pulses to lights at least this big (pixel area); 40 typical.") }),
                        Effect("particles", new()
                        {
                            ["kind"] = new { type = "string", @enum = AnimationConfig.ParticleKinds },
                            ["bbox"] = new { type = "array", items = new { type = "number" }, description = "Region [x, y, width, height] in image pixels where the particles appear." },
                            ["count"] = new { type = "integer", description = "Number of particles, 1 to 60." },
                            ["size"] = N("Particle size multiplier, 0.3 to 3. 1 typical."),
                        }),
                    }
                }
            },
            ["note"] = new { type = "string", description = "One or two plain sentences for the editor: what you chose, and anything you could not do." },
            ["needsReview"] = new { type = "boolean", description = "True if you estimated a region (e.g. particles bbox) rather than taking it from the analysis data." },
        }),
    };

    public static string AnalysisSummary(JsonObject a)
    {
        var lights = a["lights"]!.AsObject();
        var nodes = lights["nodes"]!.AsArray();
        var lines = new List<string>
        {
            $"Image size: {a["width"]} × {a["height"]} pixels. Average brightness: {Math.Round(a["brightness"]!.GetValue<double>() * 100)}%.",
            $"Lights found: {nodes.Count} larger lights (nodes), {lights["stars"]!.AsArray().Count} small star-like points.",
            lights["hub"] is JsonArray hub ? $"Hub (brightest light): [{hub[0]}, {hub[1]}]." : "No hub.",
        };
        if (nodes.Count > 0) lines.Add($"Top nodes as [x, y, area]: {new JsonArray(nodes.Take(20).Select(n => n!.DeepClone()).ToArray()).ToJsonString()}.");
        lines.Add("The photo is one flat layer (there is no cut-out of people or objects).");
        return string.Join("\n", lines);
    }

    Anthropic.AnthropicClient? client;

    // Asks Claude for settings from the admin's description. imageJpegBase64 is the photo downscaled to ~1024 px by
    // the admin's browser. Returns { config, note, needsReview } for review in the editor; nothing is saved.
    public async Task<JsonObject> Suggest(string src, string? prompt, JsonNode? current, string? imageJpegBase64)
    {
        if (!AiAvailable) throw new AnimationError("The AI isn’t set up on this server (it needs ANTHROPIC_API_KEY).");
        CheckSrc(src);
        var words = new string((prompt ?? "").Where(ch => ch == '\n' || ch == '\t' || !char.IsControl(ch)).ToArray()).Trim();
        if (words.Length > MaxPrompt) words = words[..MaxPrompt];
        if (words == "") throw new AnimationError("Describe the animation you’d like.");
        if (string.IsNullOrEmpty(imageJpegBase64) || imageJpegBase64.Length > 4_000_000)
            throw new AnimationError("The photo couldn’t be sent to the AI.");
        try { Convert.FromBase64String(imageJpegBase64); } catch { throw new AnimationError("The photo couldn’t be sent to the AI."); }

        JsonObject analysis;
        using (var c = open())
            analysis = (Row(c, src) ?? throw new AnimationError("Analyse the photo first.")).analysis;

        var text = string.Join("\n\n",
            "Analysis of the image (coordinates in image pixels of the analysed picture; the picture above shows the same image, possibly scaled):",
            AnalysisSummary(analysis),
            current is not null ? $"Current settings: {current.ToJsonString()}" : "No current settings.",
            $"The editor asks: {words}");

        // ANTHROPIC_BASE_URL (optional) points at a proxy or gateway instead of api.anthropic.com.
        var baseUrl = config["ANTHROPIC_BASE_URL"];
        client ??= string.IsNullOrWhiteSpace(baseUrl)
            ? new Anthropic.AnthropicClient { ApiKey = ApiKey, MaxRetries = 2 }
            : new Anthropic.AnthropicClient { ApiKey = ApiKey, MaxRetries = 2, BaseUrl = baseUrl.Trim() };
        Anthropic.Models.Beta.Messages.BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(new Anthropic.Models.Beta.Messages.MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                // Server-side refusal fallback: a declined request is re-served by the model Anthropic recommends.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Anthropic.Models.Beta.Messages.Default(),
                OutputConfig = new Anthropic.Models.Beta.Messages.BetaOutputConfig
                {
                    Effort = EffortName,
                    Format = new Anthropic.Models.Beta.Messages.BetaJsonOutputFormat { Schema = ModelSchema },
                },
                System = SystemPrompt,
                Messages =
                [
                    new Anthropic.Models.Beta.Messages.BetaUserMessageParam
                    {
                        Content = new List<Anthropic.Models.Beta.Messages.BetaContentBlockParam>
                        {
                            new Anthropic.Models.Beta.Messages.BetaImageBlockParam
                            {
                                Source = new Anthropic.Models.Beta.Messages.BetaBase64ImageSource { Data = imageJpegBase64, MediaType = "image/jpeg" },
                            },
                            new Anthropic.Models.Beta.Messages.BetaTextBlockParam { Text = text },
                        },
                    },
                ],
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning("Animation AI failed: {msg}", ex.Message);
            throw new AnimationError("The AI couldn’t answer just now. Try again, or use the presets.", 502);
        }

        if (response.StopReason == "refusal") throw new AnimationError("The AI declined that request. Try describing it differently, or use the presets.");
        if (response.StopReason == "max_tokens") throw new AnimationError("The AI ran out of room for the answer. Try again.", 502);
        var json = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        JsonObject output;
        try { output = JsonNode.Parse(json)!.AsObject(); }
        catch { throw new AnimationError("The AI’s answer couldn’t be read. Try again, or use the presets.", 502); }

        // Claude returns particles with a bare bbox; the player wants region.bbox.
        var effects = new JsonArray();
        foreach (var e in output["effects"] as JsonArray ?? [])
        {
            if (e is not JsonObject eo || eo["type"]?.GetValueKind() != JsonValueKind.String) continue;
            var copy = eo.DeepClone().AsObject();
            if (copy["type"]!.GetValue<string>() == "particles" && copy["bbox"] is JsonNode bb) { copy.Remove("bbox"); copy["region"] = new JsonObject { ["bbox"] = bb.DeepClone() }; }
            if (AnimationConfig.EffectTypes.Contains(copy["type"]!.GetValue<string>())) effects.Add(copy);
        }
        var candidate = output.DeepClone().AsObject();
        candidate["effects"] = effects;
        candidate["pauseButton"] = current?["pauseButton"]?.DeepClone() ?? true;
        var clean = AnimationConfig.Clean(candidate, analysis["width"]!.GetValue<int>(), analysis["height"]!.GetValue<int>());

        // Light effects with no lights to work on would do nothing: drop them and say so.
        var lights = analysis["lights"]!.AsObject();
        var nNodes = lights["nodes"]!.AsArray().Count; var nStars = lights["stars"]!.AsArray().Count; var hasHub = lights["hub"] is not null;
        var usable = new JsonArray(clean["effects"]!.AsArray().Where(e =>
        {
            var t = e!["type"]!.GetValue<string>();
            return !(t == "twinkle" && nStars == 0) && !(t == "sparkle" && nNodes == 0) && !(t == "pulses" && (!hasHub || nNodes < 3));
        }).Select(e => e!.DeepClone()).ToArray());
        var note = (output["note"]?.GetValueKind() == JsonValueKind.String ? output["note"]!.GetValue<string>() : "").Trim();
        if (note.Length > 500) note = note[..500];
        if (usable.Count < clean["effects"]!.AsArray().Count) note = $"{note} (Light effects were left out: no lights were found in this photo.)".Trim();
        clean["effects"] = usable;
        var needsReview = (output["needsReview"]?.GetValueKind() == JsonValueKind.True) || usable.Any(e => e!["type"]!.GetValue<string>() == "particles");
        return new JsonObject { ["config"] = clean, ["note"] = note, ["needsReview"] = needsReview };
    }
}
