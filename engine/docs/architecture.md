# Architecture

```text
Consumer profile + image
          |
          v
IScreenshotAnalyzer <--- OpenAiScreenshotAnalyzer
          |                 Responses API, one image, strict JSON
          v
Validated AnalysisResult
          |
          +--> Case-insensitive name candidates for a consumer's search/review workflow
          +--> JSONL records --> evaluator + labeled manifest --> report
```

## Boundaries

- **Core:** typed contracts, image loading/decoding, profile validation, context/reference validation, case-insensitive name grouping. No provider SDK or application-specific classifications.
- **OpenAI adapter:** fixed endpoint, prompt/schema, stateless request, timeout/retry handling and provenance. Uses injected `HttpClient` with the documented HTTP API rather than exposing provider types in public contracts.
- **CLI:** sequential batch orchestration and explicit live/replay modes. Emits one flushed JSONL outcome per input; saved reports are create-only. It owns no database or job queue.
- **Evaluation:** immutable fixture manifests and scoring saved records. Missing results count as failed cases and missed expected names/labels. Invalid or duplicate records fail evaluation instead of being silently dropped.
- **Consumer:** domain profile, uploads, canonical identity, alias resolution, correction history, publication policy, storage and search.

Profiles live with the consuming application. Add another profile to reuse the engine without editing core code. A future provider can implement `IScreenshotAnalyzer`; no runtime plugin registry is needed. Add crops/OCR only after a measured failure analysis shows a benefit over the full-image baseline.

## Trust and failure behavior

The prompt treats depicted text as data and grants no tools. This reduces instruction-following opportunities; it does not guarantee immunity to prompt injection. The schema constrains shape, while local validation checks labels, context references, unique IDs and cycles. Neither proves that the observations are true.

The provider envelope must finish successfully and contain one extraction. Refusals, incomplete messages, invalid JSON, invalid references and HTTP errors are explicit failures. No partial extraction is promoted to success. Raw provider error bodies and credentials are not logged.

Input images are fully decoded within byte/pixel limits before being sent. Original bytes are preserved for hashing and submission. SkiaSharp native assets are runtime dependencies; test the target deployment OS before shipping.

Result schema and prompt versions are currently `2.0`. Breaking shape changes must bump the schema version. Change the prompt version whenever its behavioral instructions change; change profile versions when guidance or labels change. The profile content hash also detects unversioned edits. No persistent database migration exists in this milestone.

## Provider references

- [Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [Structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs)
- [Images and vision](https://developers.openai.com/api/docs/guides/images-vision)

Reviewed during implementation on 2026-11-13. A model must support both image input and structured output; no model name or price is hardcoded.
