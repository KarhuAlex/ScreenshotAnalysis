using SkiaSharp;

namespace ScreenshotAnalysis.Tests;

public class CoreTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public void AcceptsSupportedImages(SKEncodedImageFormat format, string mediaType)
    {
        var image = ScreenshotImage.FromBytes(TestData.ImageBytes(format));
        Assert.Equal(mediaType, image.MediaType);
        Assert.Equal(20, image.Width);
        Assert.Equal(64, image.Sha256.Length);
    }

    [Fact]
    public void RejectsMalformedAndOversizedImages()
    {
        Assert.Throws<AnalysisException>(() => ScreenshotImage.FromBytes([]));
        Assert.Throws<AnalysisException>(() => ScreenshotImage.FromBytes("not an image"u8));
        Assert.Throws<AnalysisException>(() => ScreenshotImage.FromBytes(new byte[ScreenshotImage.MaxBytes + 1]));
        Assert.Throws<AnalysisException>(() => ScreenshotImage.FromBytes(TestData.ImageBytes()[..30]));
    }

    [Fact]
    public void GroupsCaseVariantsButPreservesPunctuationAndEvidence()
    {
        var result = TestData.Result(TestData.Image.Sha256);
        var candidate = Assert.Single(result.NameCandidates);
        Assert.Equal(new[] { "m1", "m2" }, candidate.MentionIds);
        result = result with { Extraction = result.Extraction with { Mentions = [..result.Extraction.Mentions,
            new("m3", "mira", NameRole.Mentioned, "root", "mira"),
            new("m4", "[Mira]", NameRole.GameUi, "root", "[Mira]")] } };
        Assert.Equal(2, result.NameCandidates.Length);
        Assert.Equal("Mira", result.NameCandidates[0].Name);
        Assert.Equal(new[] { "m1", "m2", "m3" }, result.NameCandidates[0].MentionIds);
        Assert.Equal("mira", result.Extraction.Mentions[2].Name);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing-parent")]
    [InlineData("duplicate-id")]
    [InlineData("unknown-label")]
    [InlineData("missing-context")]
    [InlineData("null-mentions")]
    [InlineData("blank-evidence")]
    public void RejectsInvalidExtraction(string scenario)
    {
        var extraction = TestData.Extraction;
        extraction = scenario switch
        {
            "cycle" => extraction with { Contexts = [new("root", null, "Root"), new("a", "b", "A"), new("b", "a", "B")] },
            "missing-parent" => extraction with { Contexts = [new("root", "missing", "Root")] },
            "duplicate-id" => extraction with { Mentions = [extraction.Mentions[0], extraction.Mentions[0]] },
            "unknown-label" => extraction with { Classifications = [new("not-allowed", "root", "Evidence")] },
            "missing-context" => extraction with { Mentions = [extraction.Mentions[0] with { ContextId = "missing" }] },
            "null-mentions" => extraction with { Mentions = null! },
            _ => extraction with { Mentions = [extraction.Mentions[0] with { Evidence = "" }] }
        };
        Assert.Throws<AnalysisException>(() => ExtractionValidator.Validate(extraction, TestData.Profile));
    }

    [Fact]
    public void SupportsNestedContextsAndEmptyNames()
    {
        var extraction = TestData.Extraction with
        {
            Contexts = [new("root", null, "Outer screenshot"), new("child", "root", "Embedded screenshot")],
            Mentions = []
        };
        ExtractionValidator.Validate(extraction, TestData.Profile);
    }

    [Fact]
    public void JsonRejectsMissingFieldsAndInvalidEnum()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => AnalysisJson.Read<Extraction>("{}"));
        Assert.Throws<System.Text.Json.JsonException>(() => AnalysisJson.Read<NameMention>(
            """{"id":"1","name":"X","role":"admin","contextId":"root","evidence":"X"}"""));
    }

    [Fact]
    public void RemovedSessionIdFieldIsRejectedRatherThanSilentlyReintroduced()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => AnalysisJson.Read<NameMention>(
            """{"id":"1","name":"X","role":"author","contextualId":"42","contextId":"root","evidence":"X (42): hi"}"""));
        var json = System.Text.Json.JsonSerializer.Serialize(TestData.Extraction, AnalysisJson.Options);
        Assert.DoesNotContain("contextualId", json);
        Assert.Contains("Mira (42): Hello", json);
    }
}
