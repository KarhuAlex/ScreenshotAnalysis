using System.Security.Cryptography;
using System.Text.Json;
using ScreenshotAnalysis;
using ScreenshotAnalysis.Evaluation;
using SkiaSharp;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: UpscaleFixtures <source-manifest> <new-output-directory> [integer-scale=3]");
var manifestPath = Path.GetFullPath(args[0]);
var sourceRoot = Path.GetDirectoryName(manifestPath)!;
var destination = Path.GetFullPath(args[1]);
var scale = args.Length == 3 ? int.Parse(args[2]) : 3;
if (scale is < 2 or > 8) throw new ArgumentException("Scale must be an integer between 2 and 8.");
if (Directory.Exists(destination) || File.Exists(destination))
    throw new ArgumentException("Output directory must not exist; originals and previous experiments are never overwritten.");
var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
var manifest = AnalysisJson.Read<FixtureManifest>(System.Text.Encoding.UTF8.GetString(manifestBytes));
Evaluator.ValidateManifest(manifest);
var inputs = new List<(LabeledFixture Fixture, string Path, ScreenshotImage Image)>();
foreach (var fixture in manifest.Fixtures)
{
    var path = Path.GetFullPath(fixture.Image, sourceRoot);
    var image = await ScreenshotImage.LoadAsync(path);
    if (image.Sha256 != fixture.ImageSha256) throw new ArgumentException($"Source hash mismatch: {fixture.Id}");
    if ((long)image.Width * image.Height * scale * scale > ScreenshotImage.MaxPixels)
        throw new ArgumentException($"Upscaled image exceeds pixel limit: {fixture.Id}");
    inputs.Add((fixture, path, image));
}
Directory.CreateDirectory(Path.Combine(destination, "images"));
var fixtures = new List<LabeledFixture>();
var transforms = new List<object>();
foreach (var (fixture, path, sourceImage) in inputs)
{
    using var original = SKBitmap.Decode(path) ?? throw new InvalidDataException("Source decode failed.");
    using var enlarged = new SKBitmap(new SKImageInfo(original.Width * scale, original.Height * scale,
        SKColorType.Rgba8888, SKAlphaType.Unpremul));
    for (var y = 0; y < enlarged.Height; y++)
        for (var x = 0; x < enlarged.Width; x++)
            enlarged.SetPixel(x, y, original.GetPixel(x / scale, y / scale));
    using var image = SKImage.FromBitmap(enlarged);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    var bytes = png.ToArray();
    var validated = ScreenshotImage.FromBytes(bytes);
    // Check pixel preservation after PNG encoding.
    using var decoded = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Output decode failed.");
    if (decoded.Width != original.Width * scale || decoded.Height != original.Height * scale)
        throw new InvalidDataException("Output dimensions are incorrect.");
    for (var y = 0; y < decoded.Height; y++)
        for (var x = 0; x < decoded.Width; x++)
            if (decoded.GetPixel(x, y) != original.GetPixel(x / scale, y / scale))
                throw new InvalidDataException("Output contains a changed pixel.");
    var relative = $"images/{fixtures.Count + 1:D2}.png";
    await File.WriteAllBytesAsync(Path.Combine(destination, relative), bytes);
    fixtures.Add(fixture with { Image = relative, ImageSha256 = validated.Sha256 });
    transforms.Add(new { fixture.Id, SourceImage = path, SourceSha256 = sourceImage.Sha256,
        SourceWidth = original.Width, SourceHeight = original.Height, OutputImage = relative,
        OutputSha256 = validated.Sha256, OutputWidth = decoded.Width, OutputHeight = decoded.Height,
        PixelReplicationVerified = true });
    Console.WriteLine($"{fixture.Id}: {original.Width}x{original.Height} -> {decoded.Width}x{decoded.Height}; every output pixel verified.");
    var currentSource = await ScreenshotImage.LoadAsync(path);
    if (currentSource.Sha256 != sourceImage.Sha256) throw new IOException("Source changed during processing.");
}
var derived = manifest with
{
    Dataset = $"{manifest.Dataset}-nearest-{scale}x",
    Limitations = $"Derived from {manifest.Dataset} v{manifest.Version} using {scale}x integer nearest-neighbor pixel replication and lossless PNG. No AI enhancement, interpolation, sharpening, or added information. Expected names and labels are unchanged. Provider-side resizing may affect the delivered image resolution. " + manifest.Limitations,
    Fixtures = fixtures.ToArray()
};
Evaluator.ValidateManifest(derived);
await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(derived, AnalysisJson.Options) + "\n");
await File.WriteAllTextAsync(Path.Combine(destination, "transformation.json"), JsonSerializer.Serialize(new
{
    Method = "nearest-neighbor-integer-pixel-replication", Scale = scale,
    SourceManifest = manifestPath, SourceManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
    SourceDataset = manifest.Dataset, SourceVersion = manifest.Version, Images = transforms
}, AnalysisJson.Options) + "\n");
