using System.Net;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace ScreenshotAnalysis.Tests;

internal static class TestData
{
    public static ExtractionProfile Profile => new("test", "1", [new("chat", "Chat"), new("unknown", "Unknown")], "Extract visible names.");
    public static Extraction Extraction => new([new("root", null, "Chat")], [new("chat", "root", "Chat heading")],
        [new("m1", "Mira", NameRole.Author, "root", "Mira (42): Hello"),
         new("m2", "Mira", NameRole.Author, "root", "Mira (42): Again")], []);
    public static byte[] ImageBytes(SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(20, 20);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 95);
        return encoded.ToArray();
    }
    public static ScreenshotImage Image => ScreenshotImage.FromBytes(ImageBytes());
    public static string Response(Extraction? extraction = null, string status = "completed", bool refusal = false) =>
        JsonSerializer.Serialize(new
        {
            id = "resp_test", model = "test-model-2026", status,
            usage = new { input_tokens = 100, output_tokens = 20, input_tokens_details = new { cached_tokens = 10 } },
            output = new[] { new { type = "message", status = "completed", content = new object[] {
                refusal ? new { type = "refusal", refusal = "Cannot process" } :
                    (object)new { type = "output_text", text = JsonSerializer.Serialize(extraction ?? Extraction, AnalysisJson.Options) }
            } } }
        });
    public static HttpResponseMessage Http(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    public static AnalysisResult Result(string hash) => new(Extraction, new(hash, "test", "1", new string('a', 64),
        "2.0", AnalysisContract.SchemaVersion, "test-model", "test-model-2026", "resp_test", DateTimeOffset.UnixEpoch, 10, 1, new(100, 10, 20)));
}

internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return send(request, cancellationToken);
    }
}
