## Context

Netclaw already enforces default-deny ACLs, grant-filtered tools, and coarse memory policy metadata, but those controls are evaluated in separate places and do not yet share a first-class trust model. Slack allowlists, reminder sessions, automatic recall, MCP grants, prompt-injection handling, and future webhook/public-bot paths all need a common way to answer the same question: what level of exposure is acceptable for this turn right now?

The current model is too static for upcoming scenarios:

- a personal bot may allow shell execution for the owner but must not grant the same authority to teammate DMs
- a verified GitHub webhook from a public repository is authentic transport but still carries public-tainted content
- an owner-initiated request to inspect email or other sensitive-read content should temporarily narrow what the bot may do until it returns to the owner for approval
- public bots still need useful memory, but only from public-safe audiences

This change is cross-cutting because it touches inbound source metadata, ACL evaluation, tool exposure and invocation, memory recall, MCP server classification, config defaults, diagnostics, and future adapter extensibility. The design must preserve transport-agnostic actor boundaries, remain fail-closed when policy is incomplete, and reserve room for future sandboxed execution without making it an MVP dependency.

## Goals / Non-Goals

**Goals:**
- Introduce a trust-context abstraction that computes an effective audience and capability envelope per turn.
- Keep audience cross-cutting across channels, memories, tools, MCP servers, and output effects.
- Preserve existing project/entity memory scoping while separating visibility (`audience`), subject scope (`domain`), security partition (`boundary`), and disclosure risk (`sensitivity`).
- Ensure trust can automatically narrow as the bot handles riskier content, but cannot automatically widen.
- Make incomplete or invalid policy resolve to less capability.
- Support explainability through diagnostics, doctor checks, and auditable allow/deny decisions.
- Define shell execution policy so `host-allowed` works today while `sandbox-only` is reserved as a future-safe mode.

**Non-Goals:**
- No implementation of sandboxed shell/container execution in this change.
- No Discord or webhook runtime implementation in this change; only the planning and policy contract they will rely on.
- No full prompt-injection detection engine beyond policy hooks and provenance handling.
- No attempt to solve distributed policy propagation or multi-tenant enterprise policy in MVP.

## Decisions

### Decision: Trust context is a runtime-owned composition, not a static channel flag

Each turn derives an `EffectiveTrustContext` from multiple runtime-owned inputs:

- deployment posture (`personal`, `team`, `public`)
- source/channel audience and exposure metadata
- principal classification (owner/operator, trusted internal user, public user, verified automation)
- source provenance (connector type, verified transport, source scope such as repo visibility)
- working-context downgrades (for example, while inspecting sensitive-read or public-tainted content)

The effective audience is the narrowest applicable audience in the chain. A private transport such as a Slack DM does not automatically become `personal`; a teammate DM to an owner's personal bot narrows to `team` or broader exposure.

Rationale: this unifies the user's main edge cases without hard-coding one-off exceptions per connector.

Alternative considered: keep separate channel, memory, and tool policies with ad hoc bridges. Rejected because prompt-injection and mixed-trust workflows would continue to fall through policy gaps.

### Decision: Audience is a cross-cutting visibility boundary distinct from domain and sensitivity

Add a first-class `audience` field with a small ordered ladder:

- `public`
- `team`
- `personal`

Audience answers who a fact, tool, source, or output may be exposed to. Existing `domain` continues to answer what area the item belongs to (`project:netclaw`, `project:akadana`, etc.). `sensitivity` continues to answer how harmful disclosure would be.

Operator authority remains important, but it belongs in principal classification and approval policy rather than in the audience ladder.

Rationale: project memory may exist at multiple exposure levels; overloading domain or sensitivity would blur scope and visibility.

Alternative considered: extend only `sensitivity`. Rejected because non-secret team memory and non-secret public memory still need different visibility semantics.

### Decision: Memory security boundary is distinct from domain and audience

Durable memory needs a runtime-owned `boundary` concept separate from both `domain` and `audience`.

- `audience` answers the maximum exposure level a turn may use
- `domain` answers what the memory is about (`project:netclaw`, `repo:textforge`, `person:aaron`)
- `boundary` answers which trusted partition the memory belongs to (`personal:owner`, `team:workspace`, `public:community`)

The legal retrieval universe comes from the active trust boundary and policy. Subject lookup then uses `domain`, anchors, aliases, and project/entity bindings inside that boundary. Channel or session identity is only one hint for deriving a boundary and SHALL NOT be the durable scope for reusable project facts when a broader project/entity binding is known.

Rationale: issue #203 shows that channel-derived project domains cause the bot to forget its own repository across DM and private-channel sessions, even when the knowledge should have been reusable inside the same personal or private-team trust boundary.

Alternative considered: keep using channel/session-derived domains as the primary segregation mechanism. Rejected because it hides reusable knowledge behind connector-local IDs and creates both UX failures and brittle policy semantics.

### Decision: Trust automatically downgrades, never automatically upgrades

Trust-context transitions may narrow authority whenever the bot crosses into higher-risk content or sources, such as:

- public Discord messages
- webhook payloads containing public issue comments
- email or other sensitive-read MCP results
- fetched web content

Returning to a broader capability envelope requires either explicit operator approval or a fresh trusted operator turn. The runtime must preserve provenance/taint so subtasks do not silently regain original privileges.

Rationale: this addresses prompt injection as privilege containment rather than pure text filtering.

Alternative considered: session-wide trust fixed for the session lifetime. Rejected because owner-initiated sensitive-read subtasks would still inherit too much authority.

### Decision: Tool policy has separate exposure and invocation gates

The runtime computes allowed tools before presenting them to the model, and then re-authorizes each invocation at execution time. Built-in tools and MCP tools both pass through the same policy engine, which considers posture, effective audience, grant/category, capability class, and working-context taint.

Tool capability classes include at least:

- informational/read-only
- local-read
- local-write
- sensitive-read
- publish-external / exfiltration-capable
- destructive/high-impact
- isolated-execution (reserved for future sandboxed runners)

Rationale: pre-exposure reduces attack surface; execution-time authorization catches stale or downgraded contexts.

Alternative considered: keep grant filtering only at invocation time. Rejected because offering dangerous tools to the model in low-trust contexts increases prompt-injection pressure and reasoning noise.

### Decision: Shell execution policy is modeled now, even though sandbox execution is deferred

Shell execution gets an explicit mode enum:

- `off`
- `sandbox-only`
- `host-allowed`

For this planning slice, only `off` and `host-allowed` are implementable. `sandbox-only` remains a policy-reserved future mode so the config, docs, and doctor surfaces do not need another redesign later.

Recommended posture defaults in the model:

- `personal` -> `host-allowed` for now, with future migration target to `sandbox-only`
- `team` -> `off`
- `public` -> `off`

Regardless of deployment ceiling, shell remains denied for public-tainted, teammate-DM, or sensitive-read working contexts unless a later approved policy explicitly allows it.

Rationale: preserves current utility for owner-operated bots without blocking future safer execution infrastructure.

Alternative considered: wait to model shell modes until sandboxing exists. Rejected because tool policy and config UX need a stable contract now.

### Decision: Verified transport and trusted content are evaluated separately

Inbound automation sources carry separate fields for transport authenticity and payload taint. For example:

- signed webhook from private internal repo -> authentic, narrower taint
- signed webhook from public OSS issue comment -> authentic transport, public-tainted payload

This metadata feeds trust-context derivation and doctor/explain output.

Rationale: it prevents “verified sender” from incorrectly implying “trusted content.”

Alternative considered: one trust score per connector. Rejected because it hides the most important security distinction for webhook and public-source workflows.

### Decision: Strict defaults and doctor validation are primary UX safeguards

The config schema will define policy shape, but the effective safety net is:

- posture presets and guided onboarding
- strict runtime defaults when policy is absent or partial
- doctor checks for unsafe combinations
- explain/simulate diagnostics that show effective policy

Missing policy must resolve to less capability, not startup permissiveness.

Rationale: schema validation alone cannot catch semantically unsafe combinations.

Alternative considered: require fully explicit user-authored policy for all installs. Rejected because it would be too error-prone for initial onboarding.

## Risks / Trade-offs

- [Risk] Audience, sensitivity, domain, and grants may feel overlapping to operators. -> Mitigation: keep audience small and cross-cutting, preserve domain for subject scope only, and surface effective policy explanations in doctor/CLI.
- [Risk] Trust-context derivation could become too implicit and hard to debug. -> Mitigation: log the derived context, the narrowing inputs, and the deny reasons for recall/tool decisions.
- [Risk] `host-allowed` shell in personal posture leaves residual blast radius until sandboxing exists. -> Mitigation: keep it owner-context only, default team/public to `off`, and track sandbox execution as a follow-up issue.
- [Risk] Existing memory rows lack audience and boundary metadata. -> Mitigation: define migration defaults conservatively, warn operators until rows are reclassified, and never widen legacy rows past the active boundary without explicit remapping.
- [Risk] Public bots may become too constrained to feel useful. -> Mitigation: allow public-safe memory and future isolated-execution capability without exposing host shell or sensitive-read tools.

## Migration Plan

1. Introduce trust-context and audience types in planning/specs first, with strict-default semantics.
2. Extend config schema and options to express posture, source/channel audience, shell mode, and capability classifications while defaulting absent values conservatively.
3. Propagate richer source metadata through input adapters and session command contracts.
4. Add runtime-owned security boundary derivation so memories can be partitioned by trust boundary instead of raw channel/session identity.
5. Update ACL/tool/MCP/memory policy evaluation to derive and consume `EffectiveTrustContext` plus the active boundary.
6. Add doctor/explain surfaces so operators can see effective policy, active boundary, and unsafe combinations before enabling broader exposure.
7. Defer sandbox execution to a separate implementation issue while preserving `sandbox-only` in the policy model.

Rollback strategy:

- Fall back to current ACL/grant behavior behind a feature flag if trust-context derivation causes unacceptable routing or usability regressions.
- Keep stricter deny behavior when the new policy data is missing rather than widening authority during rollback.

## Open Questions

- Should especially sensitive operator-only actions be modeled purely through principal classification and approval policy, or do we still need a separate audience later?
- How should existing durable memories be backfilled with an initial boundary without forcing immediate manual reclassification?
- Do we want a policy explain simulator in the first implementation slice, or is doctor output sufficient for MVP?
- Which built-in tools besides shell need explicit effect-class metadata in v1 of the config schema, versus inferred defaults from tool registration?
