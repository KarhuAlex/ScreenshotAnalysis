namespace ScreenshotAnalysis.Evaluation;

public record LabeledFixture(string Id, string Image, string ImageSha256, string[] Names, string[] Labels);
public record FixtureManifest(string Dataset, string Version, string Split, string Limitations, LabeledFixture[] Fixtures);
public record AnalysisFailure(string Kind, string Message);
public record RunRecord(string FixtureId, string ImageSha256, string Mode, AnalysisResult? Result,
    AnalysisFailure? Failure, double DurationMs, TokenUsage? Usage);
public record Score(long TruePositives, long FalsePositives, long FalseNegatives)
{
    public double? Precision => TruePositives + FalsePositives == 0 ? null : (double)TruePositives / (TruePositives + FalsePositives);
    public double? Recall => TruePositives + FalseNegatives == 0 ? null : (double)TruePositives / (TruePositives + FalseNegatives);
}
public record Pricing(string Model, DateOnly EffectiveDate, string Currency,
    decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion);
public record CaseScore(string Id, bool Failed, Score Names, Score Labels);
public record EvaluationConfiguration(string RequestedModel, string Model, string ProfileId,
    string ProfileVersion, string ProfileSha256, string PromptVersion, string SchemaVersion);
public record EvaluationReport(string Dataset, string DatasetVersion, string Split, string Limitations,
    string Mode, EvaluationConfiguration? Configuration, string NameMatching, int FixtureCount, int Successes, int Failures, double FailureRate, Score Names, Score Labels,
    double? MeanDurationMs, double? P95DurationMs, int UsageKnownCount, TokenUsage KnownUsage,
    Pricing? Pricing, decimal? EstimatedKnownCost, string CostLimitations, CaseScore[] Cases);
