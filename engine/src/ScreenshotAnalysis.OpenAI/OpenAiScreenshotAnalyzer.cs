using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotAnalysis.OpenAI;

public sealed record OpenAiOptions(string Model, string ApiKey)
{
    public int MaxRetries { get; init; } = 2;
    public int MaxOutputTokens { get; init; } = 12000;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>Responses API adapter. The caller owns HttpClient.</summary>
public sealed class OpenAiScreenshotAnalyzer : IScreenshotAnalyzer
{
    private readonly HttpClient client;
    private readonly OpenAiOptions options;
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");

    public OpenAiScreenshotAnalyzer(HttpClient client, OpenAiOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model) || string.IsNullOrWhiteSpace(options.ApiKey) ||
            options.MaxRetries is < 0 or > 5 || options.MaxOutputTokens < 1 ||
            options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Model, API key and valid retry, timeout and output limits are required.");
        this.client = client;
        this.options = options;
    }

    public async Task<AnalysisResult> AnalyzeAsync(ScreenshotImage image, ExtractionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        profile.Validate();
        // Keep request and validation labels consistent if the caller mutates the profile.
        var profileJson = JsonSerializer.Serialize(profile, AnalysisJson.Options);
        profile = AnalysisJson.Read<ExtractionProfile>(profileJson);
        var payload = JsonSerializer.Serialize(new
        {
            model = options.Model,
            store = false,
            instructions = ExtractionPrompt.Text,
            max_output_tokens = options.MaxOutputTokens,
            input = new[] { new { role = "user", content = new object[]
            {
                new { type = "input_text", text = "Extraction profile:\n" + profileJson },
                new { type = "input_image", image_url = image.ToDataUrl(), detail = "high" }
            } } },
            text = new { format = new { type = "json_schema", name = "screenshot_extraction",
                strict = true, schema = ExtractionSchema.Create(profile) } }
        });
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < options.MaxRetries)
                    {
                        var delay = RetryDelay(response, attempt);
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                    // Provider bodies may contain sensitive data.
                    throw new AnalysisException(AnalysisFailureKind.ProviderError,
                        $"OpenAI returned HTTP {(int)response.StatusCode} after {attempt + 1} attempt(s).");
                }
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                return Parse(body, profile, image.Sha256, profileJson, started, timer.Elapsed.TotalMilliseconds, attempt + 1);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Retrying a timed-out request could incur a second charge.
                throw new AnalysisException(AnalysisFailureKind.Timeout, "OpenAI request timed out; billing may be unknown.");
            }
            catch (HttpRequestException ex)
            {
                throw new AnalysisException(AnalysisFailureKind.ProviderError,
                    "OpenAI transport failed; request completion and billing may be unknown.", inner: ex);
            }
        }
    }

    private AnalysisResult Parse(string body, ExtractionProfile profile, string hash, string profileJson,
        DateTimeOffset started, double durationMs, int attempts)
    {
        TokenUsage? usage = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            usage = ReadUsage(root);
            var status = root.GetProperty("status").GetString();
            if (status == "incomplete")
                throw new AnalysisException(AnalysisFailureKind.Incomplete, "OpenAI returned an incomplete response.", usage);
            if (status != "completed")
                throw new AnalysisException(AnalysisFailureKind.ProviderError, "OpenAI response did not complete.", usage);
            var texts = new List<string>();
            foreach (var output in root.GetProperty("output").EnumerateArray())
            {
                if (output.GetProperty("type").GetString() != "message") continue;
                if (output.GetProperty("status").GetString() != "completed")
                    throw new AnalysisException(AnalysisFailureKind.Incomplete, "OpenAI message did not complete.", usage);
                foreach (var content in output.GetProperty("content").EnumerateArray())
                {
                    var type = content.GetProperty("type").GetString();
                    if (type == "refusal")
                        throw new AnalysisException(AnalysisFailureKind.Refusal, "OpenAI declined to analyze this image.", usage);
                    if (type == "output_text") texts.Add(content.GetProperty("text").GetString() ?? "");
                }
            }
            if (texts.Count != 1) throw new JsonException("Expected exactly one extraction output.");
            var extraction = AnalysisJson.Read<Extraction>(texts[0]);
            ExtractionValidator.Validate(extraction, profile);
            var model = root.GetProperty("model").GetString();
            if (string.IsNullOrWhiteSpace(model)) throw new JsonException("Missing response model.");
            return new(extraction, new(hash, profile.Id, profile.Version,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profileJson))),
                ExtractionPrompt.Version, ExtractionSchema.Version, options.Model, model,
                root.GetProperty("id").GetString(), started, durationMs, attempts, usage));
        }
        catch (AnalysisException ex) when (ex.Kind == AnalysisFailureKind.InvalidResponse)
        {
            throw new AnalysisException(ex.Kind, ex.Message, usage, ex);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new AnalysisException(AnalysisFailureKind.InvalidResponse, "OpenAI returned an invalid extraction response.", usage, ex);
        }
    }

    private static TokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var input = value.GetProperty("input_tokens").GetInt64();
        var output = value.GetProperty("output_tokens").GetInt64();
        var cached = value.TryGetProperty("input_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("cached_tokens", out var count)
            ? count.GetInt64() : 0;
        if (input < 0 || output < 0 || cached < 0 || cached > input) throw new JsonException("Invalid token usage.");
        return new(input, cached, output);
    }

    private static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests ||
        status == HttpStatusCode.RequestTimeout || (int)status >= 500;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        // Reject long delays rather than retrying before Retry-After.
        if (delay > TimeSpan.FromSeconds(30))
            throw new AnalysisException(AnalysisFailureKind.ProviderError, "Provider requested a retry delay exceeding 30 seconds; retry later.");
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
