using System.Security.Cryptography;
using SkiaSharp;

namespace ScreenshotAnalysis;

public sealed class ScreenshotImage
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const long MaxPixels = 25_000_000;
    private readonly byte[] bytes;
    public string MediaType { get; }
    public string Sha256 { get; }
    public int Width { get; }
    public int Height { get; }
    public string ToDataUrl() => $"data:{MediaType};base64,{Convert.ToBase64String(bytes)}";

    private ScreenshotImage(byte[] bytes, string mediaType, int width, int height)
    {
        this.bytes = bytes;
        MediaType = mediaType;
        Width = width;
        Height = height;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static ScreenshotImage FromBytes(ReadOnlySpan<byte> input)
    {
        if (input.Length is 0 or > MaxBytes) throw Invalid("Image must be between 1 byte and 20 MiB.");
        var bytes = input.ToArray();
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) throw Invalid("Image is not decodable.");
        var mediaType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => throw Invalid("Only PNG, JPEG and WebP images are supported.")
        };
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
            throw Invalid("Image exceeds the 25 megapixel limit.");
        if (codec.FrameCount > 1) throw Invalid("Animated images are not supported.");
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw Invalid("Image is truncated or corrupt.");
        return new ScreenshotImage(bytes, mediaType, info.Width, info.Height);
    }

    public static async Task<ScreenshotImage> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length is 0 or > MaxBytes) throw Invalid("Image must be between 1 byte and 20 MiB.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return FromBytes(bytes);
    }

    private static AnalysisException Invalid(string message) => new(AnalysisFailureKind.InvalidInput, message);
}
