using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotAnalysis;

public static class AnalysisJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException("JSON cannot be null.");
}

public record ClassificationLabel(string Id, string Description);
public record ExtractionProfile(string Id, string Version, ClassificationLabel[] Labels, string Guidance)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Version) ||
            string.IsNullOrWhiteSpace(Guidance) || Labels is null || Labels.Length == 0 ||
            Labels.Any(l => l is null || string.IsNullOrWhiteSpace(l.Id) || string.IsNullOrWhiteSpace(l.Description)) ||
            Labels.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != Labels.Length ||
            !Labels.Any(l => l.Id == "unknown"))
            throw new ArgumentException("Profile requires identity, version, guidance and unique labels including 'unknown'.");
    }
}

public enum NameRole { Author, Mentioned, GameUi, Unknown }
public static class AnalysisContract
{
    public const string SchemaVersion = "2.0";
}
public record SourceContext(string Id, string? ParentId, string Description);
public record ContentClassification(string Label, string ContextId, string Evidence);
public record NameMention(string Id, string Name, NameRole Role,
    string ContextId, string Evidence);
public record Extraction(SourceContext[] Contexts, ContentClassification[] Classifications,
    NameMention[] Mentions, string[] Warnings);
public record NameCandidate(string Name, string[] MentionIds);
public record TokenUsage(long InputTokens, long CachedInputTokens, long OutputTokens);
public record AnalysisProvenance(string ImageSha256, string ProfileId, string ProfileVersion,
    string ProfileSha256, string PromptVersion, string SchemaVersion, string RequestedModel,
    string Model, string? ResponseId, DateTimeOffset StartedAt, double DurationMs, int Attempts, TokenUsage? Usage);
public record AnalysisResult(Extraction Extraction, AnalysisProvenance Provenance)
{
    // Group case-insensitively, retaining the first spelling.
    public NameCandidate[] NameCandidates => GetNameCandidates(NameMatchingPolicy.OrdinalIgnoreCase);

    public NameCandidate[] GetNameCandidates(NameMatchingPolicy policy) => Extraction.Mentions
        .GroupBy(m => NameMatching.Normalize(m.Name, policy), StringComparer.OrdinalIgnoreCase)
        .Select(g => new NameCandidate(g.Key, g.Select(m => m.Id).ToArray())).ToArray();
}

public interface IScreenshotAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(ScreenshotImage image, ExtractionProfile profile,
        CancellationToken cancellationToken = default);
}

public enum AnalysisFailureKind { InvalidInput, ProviderError, Refusal, Incomplete, InvalidResponse, Timeout }
public sealed class AnalysisException(AnalysisFailureKind kind, string message, TokenUsage? usage = null,
    Exception? inner = null) : Exception(message, inner)
{
    public AnalysisFailureKind Kind { get; } = kind;
    public TokenUsage? Usage { get; } = usage;
}

public static class ExtractionValidator
{
    public static void Validate(Extraction data, ExtractionProfile profile)
    {
        profile.Validate();
        static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition) throw new AnalysisException(AnalysisFailureKind.InvalidResponse, message);
        }
        Require(data.Contexts is not null && data.Classifications is not null &&
            data.Mentions is not null && data.Warnings is not null, "Extraction arrays cannot be null.");
        var contexts = data.Contexts!;
        Require(contexts.Length > 0 && contexts.All(c => c is not null &&
            !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Description)), "Invalid contexts.");
        Require(contexts.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == contexts.Length,
            "Context IDs must be unique.");
        var byId = contexts.ToDictionary(c => c.Id, StringComparer.Ordinal);
        Require(contexts.Count(c => c.ParentId is null) == 1, "Exactly one root context is required.");
        foreach (var context in contexts)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { context.Id };
            var parent = context.ParentId;
            while (parent is not null)
            {
                Require(byId.ContainsKey(parent) && seen.Add(parent), "Invalid or cyclic context parent.");
                parent = byId[parent].ParentId;
            }
        }
        var labels = profile.Labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        Require(data.Classifications!.Length > 0 && data.Classifications.All(c => c is not null &&
            labels.Contains(c.Label) && !string.IsNullOrWhiteSpace(c.ContextId) && byId.ContainsKey(c.ContextId) && !string.IsNullOrWhiteSpace(c.Evidence)),
            "Invalid classification or context reference.");
        Require(data.Mentions!.All(m => m is not null && !string.IsNullOrWhiteSpace(m.Id) &&
            !string.IsNullOrWhiteSpace(m.Name) && m.Name == m.Name.Trim() && Enum.IsDefined(m.Role) &&
            !string.IsNullOrWhiteSpace(m.ContextId) && byId.ContainsKey(m.ContextId) && !string.IsNullOrWhiteSpace(m.Evidence)), "Invalid name mention.");
        Require(data.Mentions.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == data.Mentions.Length,
            "Mention IDs must be unique.");
        Require(data.Warnings!.All(w => !string.IsNullOrWhiteSpace(w)), "Invalid warning.");
    }
}
