## Context

`netclaw init` already provides a Termina-based wizard, but provider onboarding is currently optimized for static credential entry and not explicit about OAuth-first paths, model-catalog discovery degradation, or operator recovery after partial setup failures. This change spans onboarding UX (`netclaw-onboarding`), provider behavior contracts (`netclaw-model-providers`), and diagnostics/reporting (`netclaw-cli`) and must remain aligned with PRD-004 and PRD-005.

Architecture constraints remain unchanged: the CLI is thin where possible, runs in .NET 10, and preserves security defaults (masked secrets, fail-closed validation, default-deny assumptions). Session actors and persistence remain transport-agnostic and are not coupled to onboarding-specific provider branch logic.

## Goals / Non-Goals

**Goals:**
- Define explicit Termina decision-tree behavior for provider selection and auth-method branching during `netclaw init`.
- Define deterministic OAuth device flow states and transitions, including timeout, deny, and retry/cancel outcomes.
- Define model discovery fallback paths when provider catalog lookups fail or return incomplete data.
- Define follow-up doctor checks that verify onboarding outcomes and provide remediation-first guidance.
- Preserve existing secure input handling and keep OpenRouter as default where operator accepts defaults.

**Non-Goals:**
- Implement new provider SDK integrations beyond existing provider profile model.
- Add browser-based OAuth authorization code flow in MVP.
- Add runtime model auto-routing policies beyond existing primary/fallback semantics.
- Change actor persistence schema for sessions, tools, or Slack message handling.

## Decisions

### Decision: Represent onboarding as explicit decision trees in Termina state machine

The onboarding workflow will formalize provider setup as branching states rather than linear prompts.

Rationale:
- Makes reentrant behavior deterministic and auditable.
- Allows provider- and auth-specific validation without ambiguous transitions.

Alternatives considered:
- Keep linear prompts with conditional skips. Rejected because hidden branch behavior is hard to debug and document.
- Move provider onboarding to plain CLI mode. Rejected because PRD-004 anchors `netclaw init` as Termina TUI.

### Decision: OAuth-first providers use device flow only in MVP

For providers with OAuth support, onboarding will use device authorization flow with explicit states: `StartDeviceAuth`, `ShowUserCode`, `PollToken`, `TokenGranted`, `AuthDenied`, `AuthExpired`, `OperatorCancelled`.

Rationale:
- Works in local terminal environments without callback listeners.
- Keeps secrets out of command-line history and avoids redirect URI complexity.

Alternatives considered:
- Authorization code + local callback server. Rejected for MVP complexity and host-network edge cases.
- Manual token paste. Rejected due to high operator error rate and poor UX.

### Decision: Model discovery uses tiered fallback path with explicit provenance

Model selection will attempt, in order: (1) live provider catalog API, (2) cached last-known-good catalog, (3) curated provider defaults in config templates, (4) operator manual model entry with validation.

Rationale:
- Preserves onboarding momentum during transient provider outages.
- Maintains operator awareness of confidence level for selected model source.

Alternatives considered:
- Fail onboarding if live catalog unavailable. Rejected due to poor first-run resilience.
- Always require manual model entry. Rejected because it increases setup friction and support load.

### Decision: Doctor includes onboarding follow-up checks tied to decision-tree outputs

`netclaw doctor` will add checks that validate the resolved provider profile, effective auth method, token/API-key availability, model provenance, and fallback readiness.

Rationale:
- Connects first-run onboarding outcomes with post-onboarding troubleshooting.
- Reduces ambiguity when onboarding succeeds with degraded model discovery.

Alternatives considered:
- Keep doctor generic and rely on `config validate`. Rejected because provider auth/model issues need richer runtime-oriented remediation.

## Risks / Trade-offs

- [OAuth polling variability across providers] -> Mitigation: normalize polling interval/backoff policy in provider profile metadata and surface wait state in Termina progress components.
- [Catalog fallback picks outdated models] -> Mitigation: annotate model provenance in config and doctor output, warn when using cache/default/manual sources.
- [Increased onboarding complexity] -> Mitigation: render branch context headers in Termina ("Provider: X | Auth: OAuth device flow") and allow back-navigation without data loss.
- [Security regression from mixed credential modes] -> Mitigation: enforce masked input, redact logs, and fail closed when required auth artifacts are missing for selected branch.

## Migration Plan

1. Add spec deltas for onboarding, provider, and CLI capabilities.
2. Implement provider onboarding state machine branch metadata and transition guards.
3. Implement OAuth device flow handlers and persistence of resulting auth artifacts in existing secure config stores.
4. Implement model discovery fallback sequence and provenance tagging.
5. Extend doctor checks with provider onboarding follow-up diagnostics and remediation text.
6. Rollout behind existing onboarding path without schema-breaking config changes.

Rollback strategy:
- Revert to pre-change onboarding branch behavior while retaining backward-compatible config fields.
- Keep existing API-key path operational if OAuth-specific branches are disabled.

## Open Questions

- Which providers in MVP are marked OAuth-capable at launch versus API-key-only?
- Should doctor hard-fail (exit 1) or warn (exit 2) when model provenance is fallback-derived but still usable?
- Should onboarding cache model catalogs per provider profile or globally per provider name?
