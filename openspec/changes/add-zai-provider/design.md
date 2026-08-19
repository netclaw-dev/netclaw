## Context

Netclaw routes model access through provider descriptors, provider plugins, and `Microsoft.Extensions.AI.IChatClient`. The existing OpenAI-compatible transport supports streaming, tools, reasoning content, usage, and provider errors.

Z.ai uses an OpenAI-compatible API with required Bearer authentication. Its thinking mode defines a provider-specific request field and tool-loop replay rules. The response streams `reasoning_content`, the same field DeepSeek uses.

Session actors remain transport-agnostic. This change affects no actor message, persistence record, or session identity.

## Goals / Non-Goals

**Goals:**

- Add a first-class `zai` provider with API-key authentication.
- Reuse the existing first-party transport without changing generic provider payloads.
- Support Z.ai reasoning controls and tool-call history.
- Give operators clear setup, probe, and failure information.

**Non-Goals:**

- Add a third-party Z.ai SDK.
- Add OAuth or account and billing operations.
- Require a live Z.ai account in CI.

## Decisions

### Reuse the first-party OpenAI-compatible transport

The Z.ai plugin will construct `OpenAiCompatibleChatClient`. A required wire profile will select generic or Z.ai behavior.

This choice preserves Netclaw's media, stream, tool, usage, error, and telemetry behavior. A third-party SDK would duplicate that behavior and add supply-chain risk.

### Isolate Z.ai wire behavior

The Z.ai profile will omit local-server fields such as `return_progress`. It will serialize assistant `TextReasoningContent` as `reasoning_content` during tool-loop replay.

The generic profile will retain its current payload. This boundary prevents a Z.ai rule from changing llama.cpp, vLLM, or DwarfStar requests.

### Use MEAI reasoning options

Z.ai exposes a binary thinking toggle only. The transport will map `ReasoningEffort.None` to disabled thinking. Any other effort maps to enabled thinking.

Z.ai has no `reasoning_effort` gradation field. The transport will not emit one.

The existing Netclaw reasoning-suppression intent will map to Z.ai's disabled thinking field. Provider types will not leak into session actors.

### Use current documented model metadata

The `/models` response omits capability metadata, and context windows are documented per model id, not per family prefix. The descriptor will enrich an exact-match table: `glm-5.3` gets a one-million-token window; `glm-5.2` gets a 200,000-token window; both get text modalities. All other model IDs will retain unknown metadata.

On the coding plan, `glm-5.1`/`glm-5.2` requests are server-routed to `glm-5.3`. The `glm-5.2` enrichment therefore understates live capacity, never overstates it.

The model editor will fail visibly when it cannot resolve an unknown context window. It will not invent a fallback value.

### Default to the GLM Coding Plan endpoint

The descriptor will default to `https://api.z.ai/api/coding/paas/v4`, the endpoint for the GLM Coding Plan subscription most operators hold. Pay-as-you-go operators will override `Endpoint` with the platform base `https://api.z.ai/api/paas/v4`.

The live coding-plan `/models` endpoint returns the model list with Bearer authentication, so discovery needs no curated fallback.

### Accept any trailing version segment in endpoint resolution

`OpenAiCompatibleEndpoint.FromBaseUrl` will treat any trailing `v<digits>` path segment as an already-versioned base. The previous check matched only `/v1` and `/api/v1`, so a `v4` base produced a `/v4/v1/chat/completions` request that failed with 404.

Version-like words without digits, such as `vpreview`, will not match. Bare hosts keep the `/v1` default.

### Keep authentication fail-closed

The descriptor will expose only `ApiKeyAuth`. The probe and chat client will send the stored key as an HTTP Bearer token.

A missing key will fail before persistence or runtime use. An invalid key will produce the existing actionable provider error.

## Risks / Trade-offs

- Z.ai can change model IDs or context limits. The live catalog finds IDs, and explicit metadata applies only to known IDs.
- Z.ai can change its reasoning contract. Focused wire tests will detect payload drift.
- Generic transport edits can cause regressions. A required profile and generic payload tests isolate the change.
- A fake server cannot prove live vendor behavior. An optional live smoke test will provide final confidence.

## Migration Plan

Existing configurations require no migration. The new provider type becomes available after upgrade.

Rollback removes `zai` profiles from runtime support. Existing provider entries remain operator-owned configuration and secrets.

## Open Questions

None.
