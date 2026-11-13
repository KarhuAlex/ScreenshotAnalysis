using System.Text.Json;
using ScreenshotAnalysis.Cli;
using ScreenshotAnalysis.Evaluation;

namespace ScreenshotAnalysis.Tests;

public class NameMatchingTests
{
    [Fact]
    public void NormalizedTagsMergeZeroVariantsAndKeepEveryRawMention()
    {
        var original = TestData.Result(TestData.Image.Sha256);
        var mentions = new[]
        {
            original.Extraction.Mentions[0] with { Id = "a", Name = "R0ver", Evidence = "R0ver: hello" },
            original.Extraction.Mentions[0] with { Id = "b", Name = "rover", Evidence = "rover: hi" },
            original.Extraction.Mentions[0] with { Id = "c", Name = "[R0ver]", Evidence = "[R0ver]: bye" }
        };
        var result = original with { Extraction = original.Extraction with { Mentions = mentions } };
        Assert.Equal(3, result.NameCandidates.Length);
        var tags = result.GetNameCandidates(NameMatchingPolicy.ZeroToOIgnoreCase);
        Assert.Equal(2, tags.Length);
        Assert.Equal("ROver", tags[0].Name);
        Assert.Equal(new[] { "a", "b" }, tags[0].MentionIds);
        Assert.Equal("[ROver]", tags[1].Name);
        Assert.Equal("R0ver", result.Extraction.Mentions[0].Name);
        Assert.Equal("R0ver: hello", result.Extraction.Mentions[0].Evidence);
    }

    [Fact]
    public void ScoringNormalizesBothSidesButNotPunctuationOrOtherDigits()
    {
        var original = TestData.Result(TestData.Image.Sha256);
        var fixture = new LabeledFixture("one", "one.png", TestData.Image.Sha256,
            ["R0ver", "*Alfa*", "p1xel"], ["chat"]);
        var manifest = new FixtureManifest("test", "1", "development", "Synthetic", [fixture]);
        var result = original with { Extraction = original.Extraction with { Mentions =
            new[] { "rOver", "Alfa", "plxel" }.Select((name, i) => original.Extraction.Mentions[0] with
                { Id = $"m{i}", Name = name, Evidence = name + ": hello" }).ToArray() } };
        var record = new RunRecord("one", fixture.ImageSha256, "live", result, null, 0, result.Provenance.Usage);
        Assert.Equal(new Score(0, 3, 3), Evaluator.Evaluate(manifest, [record]).Names);
        var report = Evaluator.Evaluate(manifest, [record], nameMatching: NameMatchingPolicy.ZeroToOIgnoreCase);
        Assert.Equal(new Score(1, 2, 2), report.Names);
        Assert.Equal("zero-to-o-ordinal-ignore-case-v1", report.NameMatching);
        Assert.Equal("R0ver", fixture.Names[0]);
    }

    [Fact]
    public async Task CliExportsNormalizedTagsOfflineAndPreservesFailures()
    {
        var directory = Path.Combine(Path.GetTempPath(), "name-matching-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = TestData.Result(TestData.Image.Sha256);
            var result = original with { Extraction = original.Extraction with { Mentions =
                [original.Extraction.Mentions[0] with { Name = "R0ver" }] } };
            var compact = new JsonSerializerOptions(AnalysisJson.Options) { WriteIndented = false };
            var runs = new[] {
                new RunRecord("one", TestData.Image.Sha256, "live", result, null, 0, result.Provenance.Usage),
                new RunRecord("two", new string('b',64), "live", null, new("Refusal", "Declined"), 0, null) };
            var input = Path.Combine(directory, "runs.jsonl"); var output = Path.Combine(directory, "tags.jsonl");
            await File.WriteAllLinesAsync(input, runs.Select(r => JsonSerializer.Serialize(r, compact)));
            Assert.Equal(1, await Application.RunAsync(["tags", "--runs", input, "--out", output, "--name-matching", "zero-o"]));
            var lines = await File.ReadAllLinesAsync(output);
            using var first = JsonDocument.Parse(lines[0]); using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("ROver", first.RootElement.GetProperty("tags")[0].GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("tags").ValueKind);
            Assert.Equal("Refusal", second.RootElement.GetProperty("failure").GetProperty("kind").GetString());
            Assert.Equal(2, await Application.RunAsync(["tags", "--runs", input, "--out", output, "--name-matching", "typo"]));
        }
        finally { Directory.Delete(directory, true); }
    }
}
