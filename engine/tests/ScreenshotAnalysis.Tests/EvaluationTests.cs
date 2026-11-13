using ScreenshotAnalysis.Evaluation;

namespace ScreenshotAnalysis.Tests;

public class EvaluationTests
{
    [Fact]
    public void UnlabeledInventoryIsAcceptedForAnalysisButCannotBeScored()
    {
        var inventory = Manifest with { Split = "unlabeled" };
        Evaluator.ValidateManifest(inventory, allowUnlabeled: true);
        Assert.Throws<ArgumentException>(() => Evaluator.Evaluate(inventory, [Record]));
    }

    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static FixtureManifest Manifest => new("test", "1", "development", "Synthetic test only.",
        [new("one", "one.png", Hash, ["Mira"], ["chat"]),
         new("two", "two.png", new string('b', 64), ["Rowan"], ["chat", "game"])]);
    private static RunRecord Record => new("one", Hash, "live", TestData.Result(Hash), null, 10, new(100, 10, 20));

    [Fact]
    public void MissingResultsCountAsFailuresAndFalseNegatives()
    {
        var report = Evaluator.Evaluate(Manifest, [Record]);
        Assert.Equal(2, report.FixtureCount);
        Assert.Equal(1, report.Failures);
        Assert.Equal(.5, report.FailureRate);
        Assert.Equal(new Score(1, 0, 1), report.Names);
        Assert.Equal(new Score(1, 0, 2), report.Labels);
        Assert.Equal(.5, report.Names.Recall);
        Assert.Null(report.EstimatedKnownCost);
    }

    [Fact]
    public void IgnoresNameCaseButStillCountsMissingNames()
    {
        var result = Record.Result! with { Extraction = TestData.Extraction with { Mentions =
            [TestData.Extraction.Mentions[0] with { Name = "mira" }] } };
        var report = Evaluator.Evaluate(Manifest, [Record with { Result = result }]);
        Assert.Equal(new Score(1, 0, 1), report.Names);
        Assert.Equal("ordinal-ignore-case", report.NameMatching);
    }

    [Fact]
    public void DoesNotNormalizePunctuationOrClassificationCase()
    {
        var result = Record.Result! with { Extraction = TestData.Extraction with
        {
            Mentions = [TestData.Extraction.Mentions[0] with { Name = "[Mira]" }],
            Classifications = [new("CHAT", "root", "Chat")]
        } };
        var report = Evaluator.Evaluate(Manifest, [Record with { Result = result }]);
        Assert.Equal(new Score(0, 1, 2), report.Names);
        Assert.Equal(new Score(0, 1, 3), report.Labels);
    }

    [Fact]
    public void RejectsOldSchemaProvenance()
    {
        var result = Record.Result! with { Provenance = Record.Result.Provenance with { SchemaVersion = "1.0" } };
        Assert.Throws<ArgumentException>(() => Evaluator.Evaluate(Manifest, [Record with { Result = result }]));
    }

    [Fact]
    public void UndefinedPrecisionAndRecallAreNull()
    {
        var manifest = Manifest with { Fixtures = [Manifest.Fixtures[0] with { Names = [] }] };
        var result = Record.Result! with { Extraction = TestData.Extraction with { Mentions = [] } };
        var report = Evaluator.Evaluate(manifest, [Record with { Result = result }]);
        Assert.Null(report.Names.Precision);
        Assert.Null(report.Names.Recall);
    }

    [Fact]
    public void IncludesFailedRequestUsageInDatedCostEstimate()
    {
        var failure = new RunRecord("two", new string('b', 64), "live", null,
            new("Refusal", "Refused"), 20, new(50, 0, 5));
        var price = new Pricing("test-model", new(2026, 11, 13), "USD", 2, 1, 8);
        var report = Evaluator.Evaluate(Manifest, [Record, failure], price);
        Assert.Equal(2, report.UsageKnownCount);
        Assert.Equal(150, report.KnownUsage.InputTokens);
        Assert.Equal(.00049m, report.EstimatedKnownCost);
        Assert.Equal(20, report.P95DurationMs);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("hash")]
    [InlineData("unexpected")]
    [InlineData("ambiguous")]
    [InlineData("usage")]
    public void RejectsInvalidRunRecords(string kind)
    {
        var records = kind switch
        {
            "duplicate" => new[] { Record, Record },
            "hash" => [Record with { ImageSha256 = new string('b', 64) }],
            "unexpected" => [Record with { FixtureId = "other" }],
            "ambiguous" => [Record with { Failure = new("Refusal", "No") }],
            _ => [Record with { Usage = null }]
        };
        Assert.Throws<ArgumentException>(() => Evaluator.Evaluate(Manifest, records));
    }

    [Fact]
    public void RejectsUndatedOrWrongModelPricing()
    {
        Assert.Throws<ArgumentException>(() => Evaluator.Evaluate(Manifest, [Record], new("test-model", default, "USD", 1, 1, 1)));
        Assert.Throws<ArgumentException>(() => Evaluator.Evaluate(Manifest, [Record], new("other", new(2026, 11, 13), "USD", 1, 1, 1)));
    }

    [Fact]
    public void RejectsMixedReplayAndLiveResults()
    {
        Assert.Throws<ArgumentException>(() => Evaluator.Evaluate(Manifest,
            [Record, new("two", new string('b', 64), "replay", null, new("InvalidInput", "Missing"), 0, null)]));
    }
}
