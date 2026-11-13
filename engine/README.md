# ScreenshotAnalysis

A .NET library and CLI for AI-powered screenshot analysis using multimodal models, structured extraction and reproducible evaluation.

It preserves repeated mentions, exact spelling and nested screenshot contexts. It produces observations, not verified identities. One OpenAI vision request per image is the baseline; there is no agent loop, OCR pipeline, vector database or hosted service.

The engine combines versioned extraction profiles, validated model output, explicit inference failures and offline scoring. Saved runs retain model/prompt/profile/schema versions, image hashes, latency and token usage so extraction changes can be evaluated against the same labeled inputs.

**Status:** early working implementation with offline contract/integration tests. The included replay examples are authored fixtures, **not measured AI accuracy**. No representative real-image benchmark or model/cost comparison is included in this public release.

## Requirements

- .NET 10 SDK.
- For live analysis: an OpenAI API key and an explicitly chosen model supporting image input and structured outputs.
- Native image decoding uses SkiaSharp. Windows is locally verified; Linux and macOS assets are dependencies but those platforms have not been verified here.

All commands below run from this directory. No credentials are needed for tests or replay.

## Try the complete workflow offline

```powershell
dotnet test ScreenshotAnalysis.sln
dotnet run --project src/ScreenshotAnalysis.Cli -- analyze --manifest fixtures/development.json --profile examples/generic-profile.json --replay fixtures/replay --out artifacts/development-replay.jsonl
dotnet run --project src/ScreenshotAnalysis.Cli -- evaluate --manifest fixtures/development.json --runs artifacts/development-replay.jsonl --out artifacts/development-report.json
```

Run the same commands with `held-out` in place of `development` for the separate evaluation split. Output files are create-only: use a new output filename when rerunning. Replay exercises loading, validation, tagging, serialization and scoring; it does not call a model.

## Analyze images with OpenAI

Set `OPENAI_API_KEY` and `OPENAI_MODEL` in your environment. Do not save keys in source files. An explicit `--model` overrides `OPENAI_MODEL`.

```powershell
dotnet run --project src/ScreenshotAnalysis.Cli -- analyze --input path/to/image.png --profile examples/generic-profile.json --live --out artifacts/image.jsonl
dotnet run --project src/ScreenshotAnalysis.Cli -- analyze --input path/to/screenshots --profile examples/generic-profile.json --live --out artifacts/directory.jsonl
dotnet run --project src/ScreenshotAnalysis.Cli -- analyze --manifest fixtures/development.json --profile examples/generic-profile.json --live --out artifacts/development-live.jsonl
```

`--live` explicitly sends the selected images to OpenAI and incurs API costs. Files are sent inline, without the Files API. Requests set `store: false`; this is not a promise of zero provider retention. Directory input is nonrecursive and accepts `.png`, `.jpg`, `.jpeg`, `.webp`; content is decoded rather than trusting extensions. Limits are 20 MiB and 25 megapixels, with animated images rejected.

Processing is sequential. At most two retries follow HTTP 408, 429 or 5xx responses. Retry-After is respected up to 30 seconds; a longer delay fails the attempt with a retry-later message. Transport failures and timeouts are not replayed automatically because completion/billing may be unknown. Ctrl+C cancels; complete records already written remain usable.

Each JSONL line contains either `result` or `failure`, never both. Failed items do not prevent later items from running. Exit codes: `0` success, `1` completed with analysis failures (or an evaluation with failures/missing results), `2` invalid command/input, `130` canceled.

## Library use

```csharp
using ScreenshotAnalysis;
using ScreenshotAnalysis.OpenAI;

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var analyzer = new OpenAiScreenshotAnalyzer(http, new OpenAiOptions(
    Environment.GetEnvironmentVariable("OPENAI_MODEL")!,
    Environment.GetEnvironmentVariable("OPENAI_API_KEY")!));
var profile = AnalysisJson.Read<ExtractionProfile>(File.ReadAllText("profile.json"));
var image = await ScreenshotImage.LoadAsync("screenshot.png", cancellationToken);
var result = await analyzer.AnalyzeAsync(image, profile, cancellationToken);
foreach (var candidate in result.NameCandidates)
    Console.WriteLine($"{candidate.Name}: {candidate.MentionIds.Length} visible occurrence(s)");
```

Keep `HttpClient` long-lived in a host application. The caller owns its lifetime. `AnalysisException.Kind` distinguishes invalid input, provider errors, refusal, incomplete response, invalid response and timeout. Caller cancellation remains `OperationCanceledException`.

## Profiles and results

Profiles have an ID, version, label IDs/descriptions and domain guidance. Include the `unknown` label. A profile is trusted application configuration; depicted screenshot instructions are untrusted data. Profiles customize the task without changing the schema.

The fixed v2 result contains contexts with parent references, multiple classifications, name mentions, warnings and provenance. Evidence is model-transcribed text or a visual description, **not independently verified OCR**. Contexts describe nested surfaces; no bounding boxes are promised. Names preserve their visible spelling and punctuation. Temporary session IDs are not metadata fields or tags, although verbatim evidence can contain them. `NameCandidates` uses ordinal case-insensitive grouping and retains the first spelling; it does not identify people or merge aliases. Context and role fidelity are covered by contract fixtures but not scored by the current name-set benchmark.

Provenance includes the image SHA-256, profile identity/version/content hash, prompt/schema versions, requested and returned models, provider response ID, timestamp, duration, attempts and reported token usage. Use pinned model versions where available. Re-running inference is not guaranteed to be deterministic; re-evaluating the same saved records is.

## Evaluation

See [evaluation methodology](docs/evaluation.md) for labels, scoring, pricing and limitations, and [architecture](docs/architecture.md) for boundaries. The evaluation project is a local harness, not a published library in v1.

## Packages

For an offline image-presentation experiment, `dotnet run --project tools/UpscaleFixtures -- <source-manifest> <new-output-directory> 3` creates a separate 3x nearest-neighbor dataset. It preserves fixture IDs and labels, records transformation hashes, and verifies every output pixel after PNG encoding. It never calls a provider or overwrites an existing destination. Use the derived manifest for analysis and evaluation; compare it with a run of the original manifest using the same profile/model. Enlargement adds no source information.

```powershell
dotnet pack ScreenshotAnalysis.sln -c Release -o artifacts/packages
```

Core, OpenAI adapter and CLI tool packages are prepared locally. Nothing is published automatically. The core and adapter have no application-specific vocabulary or dependency on a consuming website.

Code and generated synthetic fixtures are MIT licensed; see [LICENSE](LICENSE). SkiaSharp and other dependencies retain their own licenses. Do not put third-party/private screenshots into a public distribution without appropriate rights.

## Schema v2 compatibility

Version 0.2.0 removes `contextualId` from name mentions and groups name candidates ignoring case. Prompt and result schema versions are `2.0`. Existing v1 live records are preserved, but the v2 reader/evaluator rejects their removed fields; rerun analysis to produce v2 records. No alias or punctuation normalization is introduced. Reports state `nameMatching: ordinal-ignore-case`; classification labels remain case-sensitive.

## Optional name-tag normalization

The raw result contract and `NameCandidates` preserve exact spelling (case-insensitive grouping). For applications that intentionally equate zero and letter O:

```csharp
var tags = result.GetNameCandidates(NameMatchingPolicy.ZeroToOIgnoreCase);
```

This replaces ASCII `0` with `O` in derived tags and merges their supporting mention IDs. It does not rewrite `Extraction.Mentions`, evidence, other digits, punctuation, or identity records. `NameMatchingPolicy.OrdinalIgnoreCase` remains the generic default.

Offline CLI commands accept `--name-matching zero-o` for matching and tag export:

```powershell
dotnet run --project src/ScreenshotAnalysis.Cli -- evaluate --manifest PATH --runs RUNS.jsonl --name-matching zero-o --out REPORT.json
dotnet run --project src/ScreenshotAnalysis.Cli -- tags --runs RUNS.jsonl --name-matching zero-o --out TAGS.jsonl
```

Reports explicitly record the matching policy. Compare experiments only under the same policy. Tag exports retain failed cases as failures with null tags, rather than successful empty tag arrays.
