using System.Text.Json;
using ScreenshotAnalysis.Cli;
using ScreenshotAnalysis.Evaluation;

namespace ScreenshotAnalysis.Tests;

public class CliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "screenshot-analysis-tests", Guid.NewGuid().ToString("N"));
    public CliTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);
    private string PathTo(string name) => Path.Combine(directory, name);
    private void Write<T>(string name, T value) => File.WriteAllText(PathTo(name), JsonSerializer.Serialize(value, AnalysisJson.Options));

    [Fact]
    public async Task ReplayAndEvaluateWorkEndToEndWithoutCredentials()
    {
        File.WriteAllBytes(PathTo("one.png"), TestData.ImageBytes());
        Write("profile.json", TestData.Profile);
        Write(TestData.Image.Sha256 + ".json", TestData.Extraction);
        var manifest = new FixtureManifest("integration", "1", "held-out", "Synthetic only.",
            [new("one", "one.png", TestData.Image.Sha256, ["Mira"], ["chat"])]);
        Write("manifest.json", manifest);
        Assert.Equal(0, await Application.RunAsync(["analyze", "--manifest", PathTo("manifest.json"), "--profile", PathTo("profile.json"),
            "--replay", directory, "--out", PathTo("runs.jsonl")]));
        Assert.Equal(0, await Application.RunAsync(["evaluate", "--manifest", PathTo("manifest.json"), "--runs", PathTo("runs.jsonl"),
            "--out", PathTo("report.json")]));
        using var report = JsonDocument.Parse(File.ReadAllText(PathTo("report.json")));
        Assert.Equal("replay", report.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, report.RootElement.GetProperty("names").GetProperty("truePositives").GetInt64());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("estimatedKnownCost").ValueKind);
        Assert.Equal(2, await Application.RunAsync(["analyze", "--input", PathTo("one.png"), "--profile", PathTo("profile.json"),
            "--replay", directory, "--out", PathTo("runs.jsonl")])); // Existing output must be rejected.
    }

    [Fact]
    public async Task DirectoryRunRecordsBadInputAndContinues()
    {
        var images = PathTo("images");
        Directory.CreateDirectory(images);
        File.WriteAllBytes(Path.Combine(images, "valid.webp"), TestData.ImageBytes()); // Deliberately mismatched extension.
        File.WriteAllText(Path.Combine(images, "bad.png"), "corrupt");
        Write("profile.json", TestData.Profile);
        Write(TestData.Image.Sha256 + ".json", TestData.Extraction);
        Assert.Equal(1, await Application.RunAsync(["analyze", "--input", images, "--profile", PathTo("profile.json"),
            "--replay", directory, "--out", PathTo("runs.jsonl")]));
        var records = File.ReadAllLines(PathTo("runs.jsonl")).Select(AnalysisJson.Read<RunRecord>).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Single(records, r => r.Result is not null);
        Assert.Single(records, r => r.Failure is not null);
    }

    [Fact]
    public async Task RequiresExplicitLiveOrReplayAndRejectsUnknownOptions()
    {
        Assert.Equal(2, await Application.RunAsync(["analyze", "--input", "x", "--profile", "x", "--out", "x"]));
        Assert.Equal(2, await Application.RunAsync(["analyze", "--typo", "x"]));
        Assert.Equal(2, await Application.RunAsync(["evaluate", "--manifest"]));
    }
}
