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
- Make operator-authored audience profiles the primary way to define filesystem, tool, and destination scope rather than relying on guessed runtime heuristics.
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

### Decision: Audience profiles define resolved capability scope; trust context selects and narrows them

Each audience level (`public`, `team`, `personal`) gets a resolved policy profile that defines what resources a turn may touch. Profiles cover at least:

- tool allow mode (`allowlist` or `all`)
- explicit built-in and MCP tool allowlists where applicable
- filesystem scopes for local-read/local-write/search/attach behaviors
- publish/external destination constraints
- shell mode and shell working-directory constraints

The runtime selects the effective profile by taking the narrowest applicable audience for the turn and then applying the matching resolved profile for that audience. A downgrade from `personal` to `public` does not merely hide some tools; it switches evaluation to the `public` profile ceiling.

Rationale: audience answers how trusted the turn is, while the profile answers what that trust level is allowed to touch. Keeping those concepts separate makes policy more explicit and operator-controlled.

Alternative considered: infer filesystem and tool scope from connector type or tool metadata alone. Rejected because ambient authority over host files and external destinations is too important to leave to guessed defaults once Netclaw is exposed beyond private owner usage.

### Decision: v1 runtime uses flattened profiles, not user-authored inheritance

Profiles may be authored internally from cumulative defaults, but runtime behavior SHALL use flattened, explicit per-audience profiles rather than inheritance rules. Operators edit the resolved profile for each audience they care about.

This means:

- `public` profile is explicit and narrow
- `team` profile is explicit and may be broader than `public`
- `personal` profile is explicit and may allow `all` modes when the operator intentionally chooses that risk

Rationale: inheritance makes security config harder to reason about and easier to misconfigure. Flattened profiles are easier to explain in doctor output and easier to validate.

Alternative considered: support full profile inheritance in config from the beginning. Rejected because subtle merge behavior would complicate MVP policy UX and diagnostics.

### Decision: Memory security boundary is distinct from domain and audience

Durable memory needs a runtime-owned `boundary` concept separate from both `domain` and `audience`.

- `audience` answers the maximum exposure level a turn may use
- `domain` answers what the memory is about (`project:netclaw`, `repo:textforge`, `person:aaron`)
- `boundary` answers which trusted partition the memory belongs to (`personal:owner`, `team:workspace`, `public:community`)

The legal retrieval universe comes from the active trust boundary and policy. Subject lookup then uses `domain`, anchors, aliases, and project/entity bindings inside that boundary. Channel or session identity is only one hint for deriving a boundary and SHALL NOT be the durable scope for reusable project facts when a broader project/entity binding is known.

Rationale: issue #203 shows that channel-derived project domains cause the bot to forget its own repository across DM and private-channel sessions, even when the knowledge should have been reusable inside the same personal or private-team trust boundary.

Alternative considered: keep using channel/session-derived domains as the primary segregation mechanism. Rejected because it hides reusable knowledge behind connector-local IDs and creates both UX failures and brittle policy semantics.

### Decision: Raw secrets are never eligible for durable memory persistence

Memory formation must treat raw credentials, private keys, bearer tokens, API secrets, and similar highly sensitive values as non-persistable content.

- `audience` does not widen this rule
- `boundary` does not widen this rule
- explicit user requests do not permit raw secret persistence

Permitted behaviors are:

- drop the candidate entirely
- persist a sanitized/redacted summary with the sensitive value removed
- emit audit or diagnostic evidence that a secret-bearing candidate was rejected

Rationale: durable memory exists to improve future reasoning, not to become a second secrets store. Retaining raw secrets in durable memory would expand blast radius across recall, tools, exports, and future integrations.

Alternative considered: allow `personal` or `manual` secret storage. Rejected because even correctly scoped secret memories create unacceptable recall and exfiltration risk compared with existing secret/config storage paths.

### Decision: Trust automatically downgrades, never automatically upgrades

Trust-context transitions may narrow authority whenever the bot crosses into higher-risk content or sources, such as:

- public Discord messages
- webhook payloads containing public issue comments
- email or other sensitive-read MCP results
- fetched web content

Returning to a broader capability envelope requires either explicit operator approval or a fresh trusted operator turn. The runtime must preserve provenance/taint so subtasks do not silently regain original privileges.

Rationale: this addresses prompt injection as privilege containment rather than pure text filtering.

Important limitation with the current memory injection model: durable memories may persist into session history once surfaced. That means a later downgrade still narrows future tool access, future recall, and future intentional retrieval, but it does not retroactively hide information already introduced earlier in the same session.

Alternative considered: session-wide trust fixed for the session lifetime. Rejected because owner-initiated sensitive-read subtasks would still inherit too much authority.

### Decision: Persisted recall makes per-turn memory policy first-contact gating

With the current memory model, recalled durable facts may be persisted into the active session history after they are first injected. Therefore:

- per-turn trust policy still governs whether a memory may be introduced into the session
- per-turn trust policy still governs future explicit retrieval and future automatic recall
- per-turn trust policy does **not** retroactively redact facts already surfaced in an earlier higher-trust turn

This means the current memory policy should be described as first-contact gating and blast-radius limitation, not as a full confidentiality barrier across mid-session trust degradation.

Rationale: this matches the runtime behavior after the memory injection overhaul and avoids claiming stronger secrecy than the system can actually provide.

Follow-up direction: if we later need stronger confidentiality across trust changes, the likely mechanism is session-scoped trust or session fork/termination on downgrade rather than attempting to scrub previously surfaced memory from history.

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

### Decision: Filesystem and publish access are controlled by explicit operator scope, not by inferred trust alone

For capabilities with ambient authority over the host or external systems, the runtime SHALL require explicit policy scope in addition to trust context and ACL grants.

Examples:

- `file_read`, `file_write`, search, and attach behaviors use configured roots such as `{session_dir}` or operator-specified repository paths
- publish/external tools use configured destination constraints such as allowed channels, recipients, or endpoint groups
- shell execution uses configured enablement plus working-directory and mode constraints

Recommended defaults remain audience-specific:

- `public` -> session directory only for local file access; no publish; no shell
- `team` -> conservative defaults, preferably session directory only unless widened explicitly
- `personal` -> may use broader defaults, but operator-authored roots and destinations are still preferred over implicit full-host access

Rationale: for public or mixed-trust deployments, guessed filesystem reach is both hard to explain and too risky. Explicit scope gives operators a clear contract and makes future public exposure safer.

Alternative considered: derive safe paths from session metadata or connector type with optional deny lists. Rejected because deny lists are incomplete and inferred scope is brittle once users start attaching real host data.

### Decision: MCP audience policy is expressed at the server level

Remote MCP servers may change their advertised tool catalogs over time as the remote operator deploys new server versions, changes entitlements, or updates dynamic registration behavior. Because of that, Netclaw SHALL treat the MCP server as the stable policy boundary for operator-facing trust configuration.

This means:

- operators classify an MCP server once with an audience ceiling and capability class
- audience profiles allow or deny whole servers, not individual remote tools, as the primary security model
- runtime may still apply internal filtering or caching behavior per tool, but those details do not replace the server-level security boundary
- if a remote server's tool catalog changes, the audience ceiling for that server still applies without requiring a policy rewrite to stay safe

Rationale: tool-level permissions for dynamic remote catalogs create a false sense of stability and are easy to invalidate overnight. Server-level audience policy is more durable, easier to explain, and fail-closed when remote catalogs evolve.

Alternative considered: operator-authored remote tool allowlists as the main policy model. Rejected for MVP because dynamic catalogs make them brittle and easy to misunderstand. Fine-grained per-tool restrictions may still exist later as an optional refinement, but not as the core safety boundary.

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

`host-allowed` shell may still be combined with an unrestricted personal profile, but that choice must be explicit and doctor output should flag it as a high-blast-radius configuration.

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
- recommended audience profiles (`public`, `team`, `personal`) with resolved defaults
- strict runtime defaults when policy is absent or partial
- doctor checks for unsafe combinations
- explain/simulate diagnostics that show effective policy

Missing policy must resolve to less capability, not startup permissiveness.

Rationale: schema validation alone cannot catch semantically unsafe combinations.

Alternative considered: require fully explicit user-authored policy for all installs. Rejected because it would be too error-prone for initial onboarding.

## Risks / Trade-offs

- [Risk] Audience, sensitivity, domain, and grants may feel overlapping to operators. -> Mitigation: keep audience small and cross-cutting, preserve domain for subject scope only, and surface effective policy explanations in doctor/CLI.
- [Risk] Operators may expect profile inheritance or wildcard behavior to work intuitively when it hides important scope edges. -> Mitigation: flatten profiles before runtime, prefer explicit `mode: all` over path wildcards for unrestricted access, and explain effective roots/destinations in doctor output.
- [Risk] Trust-context derivation could become too implicit and hard to debug. -> Mitigation: log the derived context, the narrowing inputs, and the deny reasons for recall/tool decisions.
- [Risk] `host-allowed` shell in personal posture leaves residual blast radius until sandboxing exists. -> Mitigation: keep it owner-context only, default team/public to `off`, and track sandbox execution as a follow-up issue.
- [Risk] Existing memory rows lack audience and boundary metadata. -> Mitigation: define migration defaults conservatively, warn operators until rows are reclassified, and never widen legacy rows past the active boundary without explicit remapping.
- [Risk] Public bots may become too constrained to feel useful. -> Mitigation: allow public-safe memory and future isolated-execution capability without exposing host shell or sensitive-read tools.

## Migration Plan

1. Introduce trust-context and audience types in planning/specs first, with strict-default semantics.
2. Extend config schema and options to express posture, source/channel audience, audience-scoped policy profiles, shell mode, filesystem roots, publish scopes, and capability classifications while defaulting absent values conservatively.
3. Propagate richer source metadata through input adapters and session command contracts.
4. Add runtime-owned security boundary derivation so memories can be partitioned by trust boundary instead of raw channel/session identity.
5. Update ACL/tool/MCP/memory policy evaluation to derive and consume `EffectiveTrustContext` plus the active boundary.
6. Add doctor/explain surfaces so operators can see effective profiles, active boundary, effective roots/destinations, and unsafe combinations before enabling broader exposure.
7. Defer sandbox execution to a separate implementation issue while preserving `sandbox-only` in the policy model.

Rollback strategy:

- Fall back to current ACL/grant behavior behind a feature flag if trust-context derivation causes unacceptable routing or usability regressions.
- Keep stricter deny behavior when the new policy data is missing rather than widening authority during rollback.

## Open Questions

- Should especially sensitive operator-only actions be modeled purely through principal classification and approval policy, or do we still need a separate audience later?
- How should existing durable memories be backfilled with an initial boundary without forcing immediate manual reclassification?
- Do we want a policy explain simulator in the first implementation slice, or is doctor output sufficient for MVP?
- Which built-in tools besides shell need explicit effect-class metadata in v1 of the config schema, versus inferred defaults from tool registration?
- Do we need a separate profile authoring shortcut format later, or are flattened audience profiles sufficient for the first operator UX?

## Follow-up Change Links

- `openspec/changes/sandbox-shell-execution/` implements the deferred execution path for `sandbox-only` shell mode while preserving the no-fallback security rule.
