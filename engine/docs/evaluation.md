# Evaluation methodology

## Dataset

`fixtures/development.json` and `fixtures/held-out.json` each contain four synthetic cases. Files have stable IDs and image SHA-256 hashes. Their images do not overlap. Keep held-out results out of prompt tuning; if you tune against them, retire that split and create a new held-out version.

Development covers repeated names, case/punctuation distinctions, a no-name screen, and depicted adversarial instructions. Held-out covers a nested screenshot, deleted-account placeholder, illegible author area and forum author versus mention. The images are simple generated panels, not realistic replicas of every platform. They provide a smoke dataset, not evidence of production accuracy.

The JSON files under `fixtures/replay` are deliberately authored expected extractions. They are useful for offline pipeline checks only. Perfect replay scores are expected and must never be presented as model benchmark results.

The generator is available as `dotnet run --project tools/FixtureGenerator -- <new-output-directory>`. Font rendering can differ by OS, changing image hashes. To refresh fixtures, generate into a fresh directory, inspect the images and labels, update the dataset version, and replace the complete dataset including replay files together. Do not silently regenerate the existing held-out benchmark during normal tests.

## Annotation policy

- Label all unambiguously readable visible names; preserve case, punctuation and clan tags. Exclude generic deleted-account placeholders and names that require guessing.
- A fixture's name targets are a distinct case-insensitive name set. Repeated occurrences and different source roles do not multiply its name-recall denominator.
- Numeric contextual IDs are not names. Do not infer canonical people or cross-platform equivalence.
- Assign all supported content labels; classify unknown content as `unknown`. The scorer collapses repeated labels across contexts into an image-level set.
- Roles, evidence excerpts and nested context correctness are not included in these precision/recall metrics. Inspect these separately before claiming quality in those dimensions.
- For real datasets, have a person verify labels and document ambiguous/unreadable regions before measuring. Keep private source images outside the public fixture directory.

## Metrics

Name and classification precision/recall are micro-averaged across image-level sets. Names use ordinal case-insensitive comparison (reported as `nameMatching: ordinal-ignore-case`); classification labels use ordinal case-sensitive comparison. Punctuation and digit differences still count as errors. Fuzzy matching and aliases are intentionally absent. Empty denominators yield `null`, not a fabricated perfect score. Failed or missing cases contribute expected names/labels as false negatives. Failure rate is failed-or-missing cases divided by manifest case count. Extra IDs, duplicate results, hash mismatches, mixed live/replay records and mixed successful model/profile/prompt configurations are rejected.

Reports retain split, dataset version, model/profile/prompt configuration, per-case counts, latency mean/p95 and known token usage. Latency is end-to-end CLI item duration (including decode and retry waits) for records present; missing records have no latency. Provider duration in result provenance excludes image loading. `usageKnownCount` discloses missing usage. Live reports can be recreated offline from the same manifest and JSONL records.

```powershell
dotnet run --project src/ScreenshotAnalysis.Cli -- evaluate --manifest fixtures/development.json --runs artifacts/development-live.jsonl --out artifacts/development-live-report.json
```

## Optional price configuration

No prices are bundled or fetched automatically. Supply a dated JSON file with `model` (the exact requested model), `effectiveDate` (`YYYY-MM-DD`), `currency`, `inputPerMillion`, `cachedInputPerMillion`, and `outputPerMillion`. Populate rates from the provider's applicable pricing for that date, then add `--pricing path/to/pricing.json` to evaluation.

Estimated known cost is `(uncachedInput × inputRate + cachedInput × cachedRate + output × outputRate) / 1,000,000`. It includes reported usage from failures when available. It excludes unknown usage and potentially billed retries/transport failures. It is not an invoice; report the pricing date, model and known-usage coverage alongside it. A pricing configuration with a different requested model is rejected.

## Current evidence and limitations

No live model calls or real-image accuracy measurements are included in the public fixtures. Offline tests establish workflow and contract behavior, not OCR fidelity or injection resistance. Do not select a production model or add OCR/cropping based solely on these synthetic fixtures. First collect a representative labeled development set, record the full-image baseline and categorize misses; compare changes against a held-out set and report both quality and cost.
