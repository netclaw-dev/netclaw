## Why

Netclaw's existing product and engineering docs already require contributor-facing seams that are provider-agnostic (`PRD-005`, `SPEC-008`) and transport-agnostic (`PRD-001`, `PRD-009`, `SPEC-001`), but the current runtime still carries Slack- and OpenAI-shaped assumptions across shared seams. Before open source expansion makes those assumptions harder to unwind, Netclaw needs a phased hardening pass that adds compatibility safety nets first, then extracts compiled-in provider and channel module seams without behaviorally regressing the protected OpenAI and Slack paths.

Source traceability: `PRD-001`, `PRD-004`, `PRD-005`, `PRD-009`; `SPEC-001`, `SPEC-004`, `SPEC-008`, `SPEC-010`, `SPEC-011`; `docs/spec/configuration.md`; `openspec/specs/netclaw-model-providers/spec.md`; `openspec/specs/netclaw-input-adapters/spec.md`; `openspec/specs/netclaw-testing/spec.md`; `openspec/specs/netclaw-slack-socket/spec.md`; `openspec/specs/netclaw-config-hot-reload/spec.md`.

## What Changes

- Phase 0 adds compatibility safety nets before refactors: contract and scenario coverage, protected regression paths, and validation baselines for the OpenAI API-key path, the OpenAI subscription/OAuth path, and current Slack runtime behavior.
- Tighten inference-provider extensibility around a single compiled-in provider module seam so new providers are added in one place, with no actor-contract churn and no broad plugin loader or dynamic runtime plugin system in MVP.
- Add an explicit provider-auth seam for API-key and OAuth-backed providers, with special hardening for subscription-backed provider flows and doctor/runtime/schema validation that preserves current OpenAI behavior exactly during early phases.
- Tighten channel extensibility around a single compiled-in channel module seam so new communication channels are added in one place while preserving current Slack runtime behavior during early refactors.
- Remove Slack- and OpenAI-specific assumptions from generic notification, webhook, and runtime wiring so shared flows depend on generic contracts and module seams rather than first-provider/first-channel shortcuts.
- Make value-object usage consistent at shared seams, especially where provider/channel registration, routing, notification targets, and config binding currently cross generic boundaries.
- Tighten config schema, doctor checks, startup validation, and hot-reload validation around provider, channel, and auth extension points; invalid or partial seam configuration must fail loudly with no silent fallbacks.
- Consolidate scattered low-value tests into smaller, higher-value contract and scenario suites focused on seam behavior, fail-closed validation, and the protected compatibility paths.

In scope now:
- phased seam extraction with Phase 0 safety nets first
- compiled-in provider/channel module registration and shared contract cleanup
- OAuth/auth extensibility hardening for provider onboarding and runtime validation
- generic notification/webhook/runtime decoupling needed for contributor readiness
- value-object consistency and test-suite consolidation around these seams

Out of scope later:
- dynamic plugin loading, provider marketplaces, or runtime-discovered extensions
- broad behavior changes to the protected OpenAI API-key, OpenAI OAuth/subscription, or Slack runtime paths during early phases
- new non-Slack channel implementations as part of this change
- silent compatibility shims or implicit fallback behavior for misconfigured seams

## Capabilities

### New Capabilities

- `netclaw-provider-auth`: provider authentication contracts for API-key and OAuth/subscription-backed providers, including compatibility-preserving OpenAI scenarios, token/config validation expectations, and fail-closed diagnostics guidance.
- `netclaw-runtime-notifications`: generic notification target and runtime notification contracts shared by reminders, inbound webhooks, and operational alerts so human-facing delivery does not assume Slack even when Slack remains the first implementation.

### Modified Capabilities

- `netclaw-model-providers`: tighten provider registration to a single compiled-in module seam, require contributor-safe provider extension boundaries, preserve existing OpenAI API-key and OAuth/subscription behavior during staged extraction, and require explicit validation at provider seams.
- `netclaw-input-adapters`: tighten channel registration to a single compiled-in module seam and remove Slack-specific assumptions from generic adapter/runtime contracts while preserving current Slack behavior during early phases.
- `netclaw-testing`: replace low-value seam-adjacent tests with higher-value contract and scenario suites that protect compatibility paths, fail-closed behavior, and contributor-facing extension contracts.
- `netclaw-slack-socket`: preserve current Slack Socket Mode connection, thread identity, and reply-delivery behavior while Slack moves behind the generic channel seam.
- `netclaw-config-hot-reload`: require provider, channel, auth, and notification seam configuration to validate before apply and remain fail-closed under invalid reloads, with explicit diagnostics and no silent fallback.

## Impact

- **Runtime architecture**: provider and channel extension points become explicit compiled-in modules instead of ad hoc seams spread across generic runtime code.
- **Protected compatibility paths**: OpenAI API-key, OpenAI OAuth/subscription, and Slack runtime behavior gain dedicated regression coverage before refactors begin.
- **Configuration and diagnostics**: schema, `netclaw doctor`, startup validation, and hot-reload checks must agree on provider/channel/auth invariants and reject invalid seam definitions loudly.
- **Webhook and notification alignment**: shared notification contracts must align with the existing `inbound-webhooks` change and reminder-style notification behavior without hard-coding Slack into generic flows.
- **Type safety**: shared provider/channel/auth/notification seams use consistent value objects and explicit conversions only.
- **Testing strategy**: CI shifts further toward focused contract/scenario suites instead of many narrow seam-local tests, consistent with `SPEC-010`.
- **Security impact**: extension seams remain default-deny and fail-closed; there is no plugin loader, no implicit auth downgrade, and no silent fallback from invalid or partially configured provider/channel/auth state.
- **Operational impact**: contributors get one clear seam for adding providers and one clear seam for adding channels, while operators keep existing Slack and OpenAI behavior stable throughout early phases.
