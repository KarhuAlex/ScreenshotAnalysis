using System.Net;
using System.Text.Json;
using ScreenshotAnalysis.OpenAI;

namespace ScreenshotAnalysis.Tests;

public class OpenAiTests
{
    [Fact]
    public async Task SendsOneImageWithStrictSchemaAndReturnsEvidence()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("tools", out _));
            Assert.Contains("untrusted DATA", root.GetProperty("instructions").GetString());
            var content = root.GetProperty("input")[0].GetProperty("content");
            Assert.Equal(2, content.GetArrayLength());
            Assert.StartsWith("data:image/png;base64,", content[1].GetProperty("image_url").GetString());
            Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
            AssertSchema(root.GetProperty("text").GetProperty("format").GetProperty("schema"));
            Assert.False(root.GetProperty("text").GetProperty("format").GetProperty("schema")
                .GetProperty("properties").GetProperty("mentions").GetProperty("items")
                .GetProperty("properties").TryGetProperty("contextualId", out _));
            return TestData.Http(TestData.Response());
        });
        using var client = new HttpClient(handler);
        var result = await new OpenAiScreenshotAnalyzer(client, new("test-model", "test-key"))
            .AnalyzeAsync(TestData.Image, TestData.Profile);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, result.Extraction.Mentions.Length);
        Assert.Equal("2.0", result.Provenance.SchemaVersion);
        Assert.Equal("2.0", result.Provenance.PromptVersion);
        Assert.Single(result.NameCandidates);
        Assert.Equal("test-model-2026", result.Provenance.Model);
        Assert.Equal(100, result.Provenance.Usage!.InputTokens);
        Assert.Equal(10, result.Provenance.Usage.CachedInputTokens);
    }

    [Theory]
    [InlineData("incomplete", false, AnalysisFailureKind.Incomplete)]
    [InlineData("completed", true, AnalysisFailureKind.Refusal)]
    [InlineData("failed", false, AnalysisFailureKind.ProviderError)]
    public async Task FailuresNeverBecomeEmptySuccesses(string status, bool refusal, AnalysisFailureKind expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(TestData.Http(TestData.Response(status: status, refusal: refusal))));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() =>
            new OpenAiScreenshotAnalyzer(client, new("test-model", "test-key")).AnalyzeAsync(TestData.Image, TestData.Profile));
        Assert.Equal(expected, error.Kind);
        Assert.NotNull(error.Usage);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"status\":\"completed\",\"output\":[]}")]
    public async Task RejectsInvalidEnvelopes(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(TestData.Http(body)));
        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiScreenshotAnalyzer(client, new("test-model", "test-key"))
            .AnalyzeAsync(TestData.Image, TestData.Profile));
        Assert.Equal(AnalysisFailureKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task RejectsSemanticallyInvalidModelOutput()
    {
        var extraction = TestData.Extraction with { Mentions = [TestData.Extraction.Mentions[0] with { ContextId = "missing" }] };
        using var handler = new Handler((_, _) => Task.FromResult(TestData.Http(TestData.Response(extraction))));
        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiScreenshotAnalyzer(client, new("test-model", "test-key"))
            .AnalyzeAsync(TestData.Image, TestData.Profile));
        Assert.Equal(AnalysisFailureKind.InvalidResponse, ex.Kind);
        Assert.NotNull(ex.Usage);
    }

    [Theory]
    [InlineData(429, 3)]
    [InlineData(503, 3)]
    [InlineData(401, 1)]
    [InlineData(400, 1)]
    public async Task BoundsRetriesAndDoesNotExposeProviderBody(int status, int expectedCalls)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = TestData.Http("sensitive-provider-body", (HttpStatusCode)status);
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiScreenshotAnalyzer(client, new("test-model", "test-key"))
            .AnalyzeAsync(TestData.Image, TestData.Profile));
        Assert.Equal(AnalysisFailureKind.ProviderError, ex.Kind);
        Assert.DoesNotContain("sensitive", ex.Message);
        Assert.Equal(expectedCalls, handler.Calls);
    }

    [Fact]
    public async Task RetriesTransientFailureThenSucceeds()
    {
        var calls = 0;
        using var handler = new Handler((_, _) =>
        {
            var response = ++calls == 1 ? TestData.Http("", HttpStatusCode.TooManyRequests) : TestData.Http(TestData.Response());
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var result = await new OpenAiScreenshotAnalyzer(client, new("test-model", "test-key")).AnalyzeAsync(TestData.Image, TestData.Profile);
        Assert.Equal(2, result.Provenance.Attempts);
    }

    [Fact]
    public async Task PropagatesCancellationDuringRequest()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new Handler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return TestData.Http("");
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiScreenshotAnalyzer(client, new("test-model", "test-key"))
            .AnalyzeAsync(TestData.Image, TestData.Profile, cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TimeoutIsExplicitAndNotReplayed()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return TestData.Http(""); });
        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiScreenshotAnalyzer(client,
            new("test-model", "test-key") { RequestTimeout = TimeSpan.FromMilliseconds(25) }).AnalyzeAsync(TestData.Image, TestData.Profile));
        Assert.Equal(AnalysisFailureKind.Timeout, ex.Kind);
        Assert.Equal(1, handler.Calls);
    }

    private static void AssertSchema(JsonElement schema)
    {
        if (schema.GetProperty("type").ValueKind != JsonValueKind.String) return;
        if (schema.GetProperty("type").GetString() == "object")
        {
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToHashSet();
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                Assert.Contains(property.Name, required);
                AssertSchema(property.Value);
            }
        }
        if (schema.GetProperty("type").GetString() == "array") AssertSchema(schema.GetProperty("items"));
    }
}
