## Why

Operators need text-only main models to use image context without a full model-router framework.
Netclaw also needs one reusable model lookup seam for future explicit model assignments.

Source PRDs: `PRD-001`, `PRD-004`, `PRD-005`, `PRD-009`
GitHub issue: `#1728`

## What Changes

- Add a runtime registry that resolves configured named model definitions by name.
- Add `Models.Proxies.Image` as an optional reference to a named model definition.
- Add CLI and TUI controls for the image proxy selection.
- Retain accepted image attachments when the main model is text-only and an image proxy exists.
- Ask the image proxy for one rich, OCR-aware description.
- Persist the description with source identity, proxy identity, and prompt version.
- Reuse the description for later text-only calls and after a daemon restart.
- Create missing descriptions on demand for historical images.
- Send the original image when the active main model supports image input.
- Fail visibly when proxy configuration or proxy analysis fails.

### In Scope

- Image input only.
- Named model definitions that already exist under `Models.Definitions`.
- One optional, fallback-only image proxy.
- Durable and lazy image analysis for session media.
- CLI, TUI, schema, runtime, persistence, and diagnostics support.

### Out of Scope

- Audio or video proxies.
- Targeted OCR or image-analysis tools.
- Subagent model selection.
- Dynamic per-turn routing, load balance, or the full design from issue `#648`.
- A duplicate provider or model configuration shape.

## Capabilities

### New Capabilities

- `named-model-runtime-registry`: Resolve any configured named model through one runtime lookup contract.
- `image-modality-proxy`: Create, persist, and reuse an image description for a text-only main model.

### Modified Capabilities

- `netclaw-model-providers`: Add a named image proxy assignment and fail-closed runtime validation.
- `netclaw-input-adapters`: Retain an accepted image when a configured image proxy can process it.
- `netclaw-config-command`: Let operators select or clear the image proxy through CLI and TUI model controls.
- `netclaw-model-capabilities`: Treat a durable proxy description as compatible text input while preserving the original media.

## Impact

The change affects model configuration, schema validation, daemon model setup, session persistence, media assembly, CLI, and TUI model controls.
The runtime registry reuses the current model client factory and named definition map.

### Security Impact

The proxy receives only an image that passed the existing attachment policy.
An invalid named reference blocks persistence or startup.
The runtime does not omit media or select another model without an explicit configuration.

### Operational Impact

The daemon needs a restart after a proxy configuration change.
Diagnostics identify the configured proxy and its named model definition.
Proxy failures produce visible session errors and do not call the main model.
