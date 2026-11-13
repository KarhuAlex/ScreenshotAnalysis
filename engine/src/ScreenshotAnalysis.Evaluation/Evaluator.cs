namespace ScreenshotAnalysis.Evaluation;

public static class Evaluator
{
    public static void ValidateManifest(FixtureManifest manifest, bool allowUnlabeled = false)
    {
        if (string.IsNullOrWhiteSpace(manifest.Dataset) || string.IsNullOrWhiteSpace(manifest.Version) ||
            (manifest.Split is not ("development" or "held-out") && !(allowUnlabeled && manifest.Split == "unlabeled")) || string.IsNullOrWhiteSpace(manifest.Limitations) ||
            manifest.Fixtures is null || manifest.Fixtures.Length == 0 ||
            manifest.Fixtures.Any(f => f is null || string.IsNullOrWhiteSpace(f.Id) ||
                string.IsNullOrWhiteSpace(f.Image) || !ValidHash(f.ImageSha256) || f.Names is null || f.Labels is null ||
                f.Names.Concat(f.Labels).Any(string.IsNullOrWhiteSpace)) ||
            manifest.Fixtures.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Fixtures.Length ||
            manifest.Fixtures.Select(f => f.ImageSha256).Distinct(StringComparer.Ordinal).Count() != manifest.Fixtures.Length)
            throw new ArgumentException("Invalid manifest: require a named/versioned split, unique IDs and images, hashes, and labels.");
    }

    public static EvaluationReport Evaluate(FixtureManifest manifest, IReadOnlyList<RunRecord> records, Pricing? pricing = null,
        NameMatchingPolicy nameMatching = NameMatchingPolicy.OrdinalIgnoreCase)
    {
        ValidateManifest(manifest);
        var matchingVersion = NameMatching.Version(nameMatching);
        var expected = manifest.Fixtures.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var actual = new Dictionary<string, RunRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record is null || !expected.TryGetValue(record.FixtureId, out var fixture) ||
                !actual.TryAdd(record.FixtureId, record) || record.ImageSha256 != fixture.ImageSha256 ||
                record.Mode is not ("live" or "replay") ||
                (record.Result is null) == (record.Failure is null) ||
                !double.IsFinite(record.DurationMs) || record.DurationMs < 0)
                throw new ArgumentException("Invalid, duplicate, unexpected, or mismatched run record.");
            if (record.Result is { } result)
            {
                if (result.Provenance.ImageSha256 != fixture.ImageSha256 || result.Provenance.SchemaVersion != AnalysisContract.SchemaVersion ||
                    string.IsNullOrWhiteSpace(result.Provenance.Model))
                    throw new ArgumentException("Result provenance does not match the fixture or schema.");
                // Saved results may lack the original profile.
                var labels = result.Extraction.Classifications.Select(c => c.Label).Append("unknown").Distinct()
                    .Select(l => new ClassificationLabel(l, l)).ToArray();
                ExtractionValidator.Validate(result.Extraction, new("evaluation", "1", labels, "Validate saved result."));
                if (record.Usage != result.Provenance.Usage) throw new ArgumentException("Usage disagrees with provenance.");
            }
            if (record.Usage is { } u && (u.InputTokens < 0 || u.OutputTokens < 0 || u.CachedInputTokens < 0 || u.CachedInputTokens > u.InputTokens))
                throw new ArgumentException("Invalid usage.");
        }
        if (actual.Values.Select(r => r.Mode).Distinct().Count() > 1)
            throw new ArgumentException("Live and replay records cannot be mixed.");
        var provenance = actual.Values.Where(r => r.Result is not null).Select(r => r.Result!.Provenance).ToArray();
        if (provenance.Select(p => (p.Model, p.RequestedModel, p.ProfileSha256, p.PromptVersion, p.SchemaVersion)).Distinct().Count() > 1)
            throw new ArgumentException("A report must use one model/profile/prompt configuration.");
        var cases = manifest.Fixtures.Select(f =>
        {
            actual.TryGetValue(f.Id, out var record);
            return new CaseScore(f.Id, record?.Result is null,
                Compare(f.Names.Select(n => NameMatching.Normalize(n, nameMatching)),
                    record?.Result?.GetNameCandidates(nameMatching).Select(n => n.Name) ?? [], StringComparer.OrdinalIgnoreCase),
                Compare(f.Labels, record?.Result?.Extraction.Classifications.Select(c => c.Label) ?? [], StringComparer.Ordinal));
        }).ToArray();
        var durations = actual.Values.Select(r => r.DurationMs).Order().ToArray();
        var usages = actual.Values.Where(r => r.Usage is not null).Select(r => r.Usage!).ToArray();
        var usage = new TokenUsage(usages.Sum(u => u.InputTokens), usages.Sum(u => u.CachedInputTokens), usages.Sum(u => u.OutputTokens));
        decimal? cost = null;
        if (pricing is not null)
        {
            if (pricing.EffectiveDate == default || string.IsNullOrWhiteSpace(pricing.Model) ||
                string.IsNullOrWhiteSpace(pricing.Currency) || pricing.InputPerMillion < 0 ||
                pricing.CachedInputPerMillion < 0 || pricing.OutputPerMillion < 0 ||
                provenance.Length == 0 || provenance.Any(p => p.RequestedModel != pricing.Model))
                throw new ArgumentException("Pricing requires a date, currency, nonnegative rates and matching requested model.");
            cost = ((usage.InputTokens - usage.CachedInputTokens) * pricing.InputPerMillion +
                usage.CachedInputTokens * pricing.CachedInputPerMillion + usage.OutputTokens * pricing.OutputPerMillion) / 1_000_000m;
        }
        var failures = cases.Count(c => c.Failed);
        var first = provenance.FirstOrDefault();
        var configuration = first is null ? null : new EvaluationConfiguration(first.RequestedModel, first.Model,
            first.ProfileId, first.ProfileVersion, first.ProfileSha256, first.PromptVersion, first.SchemaVersion);
        return new(manifest.Dataset, manifest.Version, manifest.Split, manifest.Limitations,
            actual.Values.FirstOrDefault()?.Mode ?? "no-records", configuration, matchingVersion, cases.Length, cases.Length - failures, failures,
            (double)failures / cases.Length, Sum(cases.Select(c => c.Names)), Sum(cases.Select(c => c.Labels)),
            durations.Length == 0 ? null : durations.Average(),
            durations.Length == 0 ? null : durations[(int)Math.Ceiling(durations.Length * .95) - 1],
            usages.Length, usage, pricing, cost,
            "Known usage only; missing usage and unsuccessful/retried requests may incur additional cost. Replay is not model performance or a live bill.", cases);
    }

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static Score Compare(IEnumerable<string> expected, IEnumerable<string> actual, StringComparer comparer)
    {
        var e = expected.ToHashSet(comparer);
        var a = actual.ToHashSet(comparer);
        return new(e.Intersect(a, comparer).LongCount(), a.Except(e, comparer).LongCount(), e.Except(a, comparer).LongCount());
    }
    private static Score Sum(IEnumerable<Score> scores) => new(scores.Sum(s => s.TruePositives),
        scores.Sum(s => s.FalsePositives), scores.Sum(s => s.FalseNegatives));
}
