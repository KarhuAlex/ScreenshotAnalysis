using ScreenshotAnalysis.Evaluation;

namespace ScreenshotAnalysis.Tests;

public class FixtureTests
{
    [Fact]
    public async Task PublicFixturesAreDecodableDisjointAndHaveValidAuthoredReplays()
    {
        var profile = AnalysisJson.Read<ExtractionProfile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "generic-profile.json")));
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var seen = new HashSet<string>();
        foreach (var split in new[] { "development", "held-out" })
        {
            var manifest = AnalysisJson.Read<FixtureManifest>(File.ReadAllText(Path.Combine(root, split + ".json")));
            Evaluator.ValidateManifest(manifest);
            Assert.Equal(split, manifest.Split);
            Assert.Equal(4, manifest.Fixtures.Length);
            foreach (var fixture in manifest.Fixtures)
            {
                var image = await ScreenshotImage.LoadAsync(Path.Combine(root, fixture.Image));
                Assert.Equal(fixture.ImageSha256, image.Sha256);
                Assert.True(seen.Add(image.Sha256), "Fixture duplicated across dataset splits.");
                var extraction = AnalysisJson.Read<Extraction>(File.ReadAllText(Path.Combine(root, "replay", image.Sha256 + ".json")));
                ExtractionValidator.Validate(extraction, profile);
                Assert.Equal(fixture.Names.Order(), extraction.Mentions.Select(m => m.Name).Distinct().Order());
                Assert.Equal(fixture.Labels.Order(), extraction.Classifications.Select(c => c.Label).Distinct().Order());
            }
        }
    }
}
