using SkiaSharp;

namespace CoffeeLoyalty.Branding;

/// <summary>
/// Processes client uploads on the way in. Clients upload photos straight off a phone, so
/// nothing reaches disk un-resized: decode, honour EXIF orientation, drop all metadata by
/// re-encoding, cap dimensions. The square icon variants are generated rather than asked for.
///
/// SkiaSharp (MIT) rather than ImageSharp — ImageSharp v4 requires a paid licence key at
/// build time, which is not appropriate for a product sold to clients.
/// </summary>
public static class ImagePipeline
{
    /// <summary>Longest edge, per logical asset. Hero is a background; logos are small.</summary>
    private static readonly Dictionary<string, int> MaxEdge = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logo"] = 512,
        ["hero"] = 1200,
        ["favicon"] = 180,
        ["icon192"] = 192,
        ["icon512"] = 512,
        ["iconMaskable"] = 512,
        ["walletLogo"] = 660   // Google Wallet renders programLogo at up to 660px
    };

    public const long MaxUploadBytes = 12L * 1024 * 1024;

    /// <summary>Assets derived automatically whenever the logo is replaced.</summary>
    public static readonly string[] DerivedFromLogo = { "icon192", "icon512", "iconMaskable", "favicon" };

    /// <summary>
    /// Reverting an asset to the platform default. Dropping the logo also drops the icons
    /// generated from it, otherwise the PWA keeps serving icons cut from the old logo.
    /// </summary>
    public static void Revert(AssetResolver assets, string name)
    {
        assets.RemoveUpload(name);
        if (name.Equals("logo", StringComparison.OrdinalIgnoreCase))
            foreach (var derived in DerivedFromLogo) assets.RemoveUpload(derived);
    }

    public sealed record Result(bool Ok, string? Error = null, int Width = 0, int Height = 0, long Bytes = 0);

    public static async Task<Result> SaveAsync(AssetResolver assets, string name, Stream input, string contentType)
    {
        if (!AssetResolver.IsKnown(name)) return new Result(false, $"Unknown asset '{name}'");

        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        if (buffer.Length == 0) return new Result(false, "Empty upload");
        if (buffer.Length > MaxUploadBytes) return new Result(false, "File is larger than 12 MB");
        buffer.Position = 0;

        if (contentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
            return SaveSvg(assets, name, buffer);

        // Decoded by content, never by the client-supplied extension or content type.
        using var original = Decode(buffer);
        if (original is null) return new Result(false, "That file is not a readable image");

        using var sized = Fit(original, MaxEdge.GetValueOrDefault(name, 512));
        assets.RemoveUpload(name);
        var path = assets.UploadPath(name, ".png");
        Write(sized, path);

        if (name.Equals("logo", StringComparison.OrdinalIgnoreCase)) GenerateIcons(assets, sized);

        return new Result(true, Width: sized.Width, Height: sized.Height, Bytes: new FileInfo(path).Length);
    }

    /// <summary>
    /// Decodes and applies the EXIF orientation. Re-encoding later drops every metadata
    /// block — including the GPS coordinates phone photos carry — but orientation has to be
    /// baked into the pixels first or portrait uploads appear sideways.
    /// </summary>
    private static SKBitmap? Decode(Stream stream)
    {
        stream.Position = 0;
        using var codec = SKCodec.Create(stream);
        if (codec is null) return null;

        var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return null;

        return codec.EncodedOrigin == SKEncodedOrigin.TopLeft ? bitmap : Reorient(bitmap, codec.EncodedOrigin);
    }

    private static SKBitmap Reorient(SKBitmap source, SKEncodedOrigin origin)
    {
        using var _ = source;
        var swapAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                              or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var target = new SKBitmap(swapAxes ? source.Height : source.Width,
                                  swapAxes ? source.Width : source.Height);

        using var canvas = new SKCanvas(target);
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1).PostConcat(SKMatrix.CreateTranslation(source.Width, 0)),
            SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, source.Width / 2f, source.Height / 2f),
            SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1).PostConcat(SKMatrix.CreateTranslation(0, source.Height)),
            SKEncodedOrigin.LeftTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateScale(1, -1))
                                              .PostConcat(SKMatrix.CreateTranslation(0, 0)),
            SKEncodedOrigin.RightTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(source.Height, 0)),
            SKEncodedOrigin.RightBottom => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateScale(1, -1))
                                                  .PostConcat(SKMatrix.CreateTranslation(source.Height, source.Width)),
            SKEncodedOrigin.LeftBottom => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, source.Width)),
            _ => SKMatrix.CreateIdentity()
        };
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

    /// <summary>Scales down to fit inside a square of <paramref name="maxEdge"/>, preserving aspect.</summary>
    private static SKBitmap Fit(SKBitmap source, int maxEdge)
    {
        if (source.Width <= maxEdge && source.Height <= maxEdge) return source.Copy();

        var scale = Math.Min((float)maxEdge / source.Width, (float)maxEdge / source.Height);
        var info = new SKImageInfo((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale));
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? source.Copy();
    }

    /// <summary>Square canvas with the image centred — launchers crop maskable icons to a circle.</summary>
    private static SKBitmap Pad(SKBitmap source, int size)
    {
        var target = new SKBitmap(size, size);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        using var fitted = Fit(source, size);
        canvas.DrawBitmap(fitted, (size - fitted.Width) / 2f, (size - fitted.Height) / 2f);
        return target;
    }

    private static void Write(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    private static void GenerateIcons(AssetResolver assets, SKBitmap logo)
    {
        foreach (var derived in DerivedFromLogo)
        {
            var size = MaxEdge.GetValueOrDefault(derived, 192);
            using var icon = derived == "iconMaskable" ? Pad(logo, size) : Fit(logo, size);
            assets.RemoveUpload(derived);
            Write(icon, assets.UploadPath(derived, ".png"));
        }
    }

    private static Result SaveSvg(AssetResolver assets, string name, MemoryStream buffer)
    {
        var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        if (!text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            return new Result(false, "That file is not a valid SVG");
        if (text.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || text.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
            return new Result(false, "SVGs containing scripts are not accepted");

        assets.RemoveUpload(name);
        File.WriteAllText(assets.UploadPath(name, ".svg"), text);
        return new Result(true, Bytes: buffer.Length);
    }
}
