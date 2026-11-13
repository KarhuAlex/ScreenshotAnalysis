using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenshotAnalysis.Evaluation;
using ScreenshotAnalysis.OpenAI;

namespace ScreenshotAnalysis.Cli;

public static class Application
{
    private const string Help = """
        ScreenshotAnalysis — evidence-backed screenshot extraction
        analyze (--input FILE_OR_DIRECTORY | --manifest FILE) --profile FILE --out FILE.jsonl
                (--live [--model MODEL] | --replay DIRECTORY)
        evaluate --manifest FILE --runs FILE.jsonl --out FILE.json [--pricing FILE] [--name-matching exact|zero-o]
        tags --runs FILE.jsonl --out FILE.jsonl [--name-matching exact|zero-o]

        Live mode sends images to OpenAI. It requires OPENAI_API_KEY and --model or OPENAI_MODEL.
        Replay reads <image-sha256>.json extraction fixtures; it is NOT an AI benchmark.
        Directory input is nonrecursive. Output files must not already exist.
        Exit codes: 0 success; 1 analysis/evaluation includes failures; 2 invalid command/input; 130 canceled.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["help"])
            {
                Console.WriteLine(Help);
                return 0;
            }
            var options = Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "analyze" => await Analyze(options, cancellationToken),
                "evaluate" => await Evaluate(options, cancellationToken),
                "tags" => await Tags(options, cancellationToken),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Canceled. Completed JSONL records remain available for evaluation.");
            return 130;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or AnalysisException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<int> Analyze(Dictionary<string, string> options, CancellationToken ct)
    {
        Allowed(options, "input", "manifest", "profile", "out", "live", "model", "replay");
        if (options.ContainsKey("input") == options.ContainsKey("manifest"))
            throw new ArgumentException("Specify exactly one of --input and --manifest.");
        if (options.ContainsKey("live") == options.ContainsKey("replay"))
            throw new ArgumentException("Specify exactly one of --live and --replay.");
        if (options.ContainsKey("model") && !options.ContainsKey("live"))
            throw new ArgumentException("--model applies only to --live.");
        var profile = await Read<ExtractionProfile>(Required(options, "profile"), ct);
        profile.Validate();
        var inputs = await Inputs(options, ct);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        IScreenshotAnalyzer analyzer = options.TryGetValue("replay", out var replay)
            ? new ReplayAnalyzer(replay)
            : new OpenAiScreenshotAnalyzer(client, new(
                options.GetValueOrDefault("model") ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "",
                Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""));
        var mode = options.ContainsKey("live") ? "live" : "replay";
        await using var stream = NewOutput(Required(options, "out"));
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        var failed = false;
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            RunRecord record;
            var hash = input.Hash ?? "";
            try
            {
                var image = await ScreenshotImage.LoadAsync(input.Path, ct);
                hash = image.Sha256;
                if (input.Hash is not null && input.Hash != hash)
                    throw new AnalysisException(AnalysisFailureKind.InvalidInput, "Fixture image hash does not match its manifest.");
                var result = await analyzer.AnalyzeAsync(image, profile, ct);
                record = new(input.Id, hash, mode, result, null, timer.Elapsed.TotalMilliseconds, result.Provenance.Usage);
            }
            catch (Exception ex) when (ex is AnalysisException or IOException or UnauthorizedAccessException or JsonException)
            {
                failed = true;
                record = new(input.Id, input.Hash ?? hash, mode, null,
                    new(ex is AnalysisException ae ? ae.Kind.ToString() : "InvalidInput", ex.Message),
                    timer.Elapsed.TotalMilliseconds, (ex as AnalysisException)?.Usage);
                Console.Error.WriteLine($"{input.Id}: {record.Failure!.Kind}");
            }
            // Preserve completed results if the batch is interrupted.
            var json = JsonSerializer.Serialize(record, CompactJson);
            await writer.WriteLineAsync(json.AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        return failed ? 1 : 0;
    }

    private static async Task<int> Evaluate(Dictionary<string, string> options, CancellationToken ct)
    {
        Allowed(options, "manifest", "runs", "out", "pricing", "name-matching");
        var manifest = await Read<FixtureManifest>(Required(options, "manifest"), ct);
        var lines = await File.ReadAllLinesAsync(Required(options, "runs"), ct);
        var records = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(AnalysisJson.Read<RunRecord>).ToArray();
        var pricing = options.TryGetValue("pricing", out var path) ? await Read<Pricing>(path, ct) : null;
        var matching = NameMatching.Parse(options.GetValueOrDefault("name-matching") ?? "exact");
        var report = Evaluator.Evaluate(manifest, records, pricing, matching);
        await using var output = NewOutput(Required(options, "out"));
        await JsonSerializer.SerializeAsync(output, report, AnalysisJson.Options, ct);
        Console.WriteLine($"{report.Mode}: {report.FixtureCount} fixtures, {report.Failures} failures. Report written.");
        return report.Failures > 0 ? 1 : 0;
    }

    private static async Task<int> Tags(Dictionary<string, string> options, CancellationToken ct)
    {
        Allowed(options, "runs", "out", "name-matching");
        var policy = NameMatching.Parse(options.GetValueOrDefault("name-matching") ?? "exact");
        var lines = await File.ReadAllLinesAsync(Required(options, "runs"), ct);
        var records = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(AnalysisJson.Read<RunRecord>).ToArray();
        if (records.Any(r => (r.Result is null) == (r.Failure is null)))
            throw new ArgumentException("Each run must contain exactly one result or failure.");
        foreach (var record in records.Where(r => r.Result is not null))
        {
            var result = record.Result!;
            if (result.Provenance.ImageSha256 != record.ImageSha256 || result.Provenance.SchemaVersion != AnalysisContract.SchemaVersion)
                throw new ArgumentException("Result provenance does not match its run or schema.");
            var labels = result.Extraction.Classifications.Select(c => c.Label).Append("unknown").Distinct()
                .Select(label => new ClassificationLabel(label, label)).ToArray();
            ExtractionValidator.Validate(result.Extraction, new("tag-export", "1", labels, "Validate saved extraction."));
        }
        await using var output = NewOutput(Required(options, "out"));
        await using var writer = new StreamWriter(output, new UTF8Encoding(false));
        foreach (var record in records)
        {
            var tags = new { record.FixtureId, record.ImageSha256, NameMatching = NameMatching.Version(policy),
                Tags = record.Result?.GetNameCandidates(policy), record.Failure };
            await writer.WriteLineAsync(JsonSerializer.Serialize(tags, CompactJson).AsMemory(), ct);
        }
        return records.Any(r => r.Failure is not null) ? 1 : 0;
    }

    private sealed record Input(string Id, string Path, string? Hash);
    private static async Task<Input[]> Inputs(Dictionary<string, string> options, CancellationToken ct)
    {
        if (options.TryGetValue("manifest", out var manifestPath))
        {
            var manifest = await Read<FixtureManifest>(manifestPath, ct);
            Evaluator.ValidateManifest(manifest, allowUnlabeled: true);
            var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            return manifest.Fixtures.Select(f => new Input(f.Id, Path.GetFullPath(f.Image, root), f.ImageSha256)).ToArray();
        }
        var path = Required(options, "input");
        if (!Directory.Exists(path)) return [new(Path.GetFileName(path), Path.GetFullPath(path), null)];
        var extensions = new HashSet<string>([".png", ".jpg", ".jpeg", ".webp"], StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(path).Where(p => extensions.Contains(Path.GetExtension(p)))
            .Order(StringComparer.Ordinal).Select(p => new Input(Path.GetFileName(p), Path.GetFullPath(p), null)).ToArray();
        if (files.Length == 0) throw new ArgumentException("No supported images found in directory.");
        return files;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Options must start with --.");
            var name = args[i][2..];
            var value = "true";
            if (name != "live")
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for --{name}.");
                value = args[i];
            }
            if (!result.TryAdd(name, value)) throw new ArgumentException($"Duplicate option --{name}.");
        }
        return result;
    }
    private static void Allowed(Dictionary<string, string> options, params string[] names)
    {
        if (options.Keys.Except(names).FirstOrDefault() is { } invalid) throw new ArgumentException($"Unknown option --{invalid}.");
    }
    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value :
            throw new ArgumentException($"--{name} is required.");
    private static async Task<T> Read<T>(string path, CancellationToken ct) => AnalysisJson.Read<T>(await File.ReadAllTextAsync(path, ct));
    private static FileStream NewOutput(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
    private static readonly JsonSerializerOptions CompactJson = new(AnalysisJson.Options) { WriteIndented = false };

    private sealed class ReplayAnalyzer(string directory) : IScreenshotAnalyzer
    {
        public async Task<AnalysisResult> AnalyzeAsync(ScreenshotImage image, ExtractionProfile profile, CancellationToken cancellationToken)
        {
            var extraction = await Read<Extraction>(Path.Combine(directory, image.Sha256 + ".json"), cancellationToken);
            ExtractionValidator.Validate(extraction, profile);
            var profileHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, AnalysisJson.Options))));
            return new(extraction, new(image.Sha256, profile.Id, profile.Version, profileHash,
                "replay-2", AnalysisContract.SchemaVersion, "offline-replay", "offline-replay", null, DateTimeOffset.UtcNow, 0, 0, null));
        }
    }
}
