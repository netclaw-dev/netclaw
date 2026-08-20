## Context

Netclaw routes model access through provider descriptors, provider plugins, and `Microsoft.Extensions.AI.IChatClient`. The existing OpenAI-compatible transport supports streaming, tools, reasoning content, usage, and provider errors.

DeepSeek uses an OpenAI-compatible API with required Bearer authentication. Its thinking mode also defines provider-specific request fields and tool-loop replay rules.

Session actors remain transport-agnostic. This change affects no actor message, persistence record, or session identity.

## Goals / Non-Goals

**Goals:**

- Add a first-class `deepseek` provider with API-key authentication.
- Reuse the existing first-party transport without changing generic provider payloads.
- Support DeepSeek reasoning controls and tool-call history.
- Give operators clear setup, probe, and failure information.

**Non-Goals:**

- Add a third-party DeepSeek SDK.
- Add OAuth or account and billing operations.
- Use DeepSeek beta endpoints.
- Require a live DeepSeek account in CI.

## Decisions

### Reuse the first-party OpenAI-compatible transport

The DeepSeek plugin will construct `OpenAiCompatibleChatClient`. A required wire profile will select generic or DeepSeek behavior.

This choice preserves Netclaw's media, stream, tool, usage, error, and telemetry behavior. A third-party SDK would duplicate that behavior and add supply-chain risk.

### Isolate DeepSeek wire behavior

The DeepSeek profile will omit local-server fields such as `return_progress`. It will serialize assistant `TextReasoningContent` as `reasoning_content` during tool-loop replay.

The generic profile will retain its current payload. This boundary prevents a DeepSeek rule from changing llama.cpp, vLLM, or DwarfStar requests.

### Use MEAI reasoning options

The transport will map `ReasoningEffort.None` to disabled thinking. Low, medium, and high effort map to DeepSeek `high`; extra-high maps to `max`.

The existing Netclaw reasoning-suppression intent will map to DeepSeek's disabled thinking field. Provider types will not leak into session actors.

### Use current documented model metadata

The descriptor will add a one-million-token context window and text modalities to `deepseek-v4-flash` and `deepseek-v4-pro`. Unknown model IDs will retain unknown metadata.

The model editor will fail visibly when it cannot resolve an unknown context window. It will not invent a fallback value.

### Keep authentication fail-closed

The descriptor will expose only `ApiKeyAuth`. The probe and chat client will send the stored key as an HTTP Bearer token.

A missing key will fail before persistence or runtime use. An invalid key will produce the existing actionable provider error.

## Risks / Trade-offs

- DeepSeek can change model IDs or context limits. The live catalog finds IDs, and explicit metadata applies only to known IDs.
- DeepSeek can change its reasoning contract. Focused wire tests will detect payload drift.
- Generic transport edits can cause regressions. A required profile and generic payload tests isolate the change.
- A fake server cannot prove live vendor behavior. An optional live smoke test will provide final confidence.

## Migration Plan

Existing configurations require no migration. The new provider type becomes available after upgrade.

Rollback removes `deepseek` profiles from runtime support. Existing provider entries remain operator-owned configuration and secrets.

## Open Questions

None.
