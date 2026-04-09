## Context

Netclaw already documents provider-agnostic model access, transport-agnostic
session commands, and fail-closed validation, but the current runtime still has
first-provider and first-channel assumptions leaking across shared seams.
`netclaw-model-providers` requires provider interactions to route through
`IChatClient`, `netclaw-input-adapters` requires adapters to emit generic
`SendUserMessage` commands, `netclaw-config-hot-reload` requires validate-before-
apply behavior, and `netclaw-testing` already expects provider-independent CI.

This change is a cross-cutting contributor-hardening program that makes those
seams explicit before broader OSS contribution expands the number of touched
paths. The design protects three compatibility-critical paths first:

- OpenAI API-key inference path
- OpenAI OAuth/subscription path
- Slack runtime behavior, especially Socket Mode connection, thread routing, and
  reply delivery

Those paths are protected early because they are the highest-risk combinations
of user-visible behavior and seam complexity. If refactors regress them, the
project becomes harder to validate, harder to review, and harder to trust.

This design is constrained by current MVP boundaries:

- single-process host
- actor-driven session runtime with transport-agnostic session contracts
- default-deny security posture
- loud validation failures with no silent fallbacks
- no dynamic plugin loading in MVP

Stakeholders are maintainers extending provider/channel support, OSS
contributors validating changes from a fresh clone, and operators who need
existing Slack and OpenAI behavior preserved while the seams are hardened.

## Goals / Non-Goals

**Goals:**

- Introduce one compiled-in provider module seam for all inference providers.
- Introduce one compiled-in channel module seam for all human-facing and
  machine-facing channel adapters.
- Introduce one provider-auth seam that separates token acquisition, token
  refresh, persistence, and token-to-runtime-client mapping while preserving
  OpenAI as the first implementation.
- Replace free-form seam strings at provider/channel/notification boundaries
  with shared value objects and explicit conversions.
- Remove Slack-specific target kinds and tool names from generic notification,
  reminder, and inbound webhook flows.
- Make schema validation, doctor, startup validation, and hot reload enforce
  the same invariants and fail loudly on the same invalid states.
- Shift test coverage toward compatibility contract tests and broader scenario
  tests, with Phase 0 safety nets added before refactors.
- Keep actor boundaries transport-agnostic and avoid persistence churn outside
  explicit boundary-value changes.
- Make the change merge-safe through phased milestones that can land
  independently without breaking protected paths.

**Non-Goals:**

- Dynamic plugin loading, provider marketplaces, or runtime-discovered
  extensions.
- Shipping new non-Slack channels in this change.
- Replacing OpenAI or Slack as the primary compatibility baseline.
- Introducing silent compatibility shims, permissive auth downgrades, or hidden
  fallback providers/channels.
- Rewriting actor architecture, persistence strategy, or session identity.
- Solving every future extension point in one pass; this change establishes the
  seam model for MVP.

## Decisions

### 1. Use compiled-in module registries for provider and channel extensibility

Netclaw will converge on two explicit registries:

- `ProviderModule` seam for inference providers
- `ChannelModule` seam for input/output channels and delivery adapters

Each module is compiled into the product and registered centrally during host
startup. Generic runtime code consumes these registries through typed module
descriptors rather than ad hoc `if provider == "openai"` or Slack-specific
branching.

Rationale:

- Gives contributors one obvious place to add a provider and one obvious place
  to add a channel.
- Preserves MVP simplicity and static reviewability.
- Supports fail-closed startup: unknown module kinds cannot appear at runtime if
  they are not compiled in and schema-approved.

Alternatives considered:

- Dynamic plugin loading: rejected because it complicates startup validation,
  dependency management, security review, and reproducible contributor setups.
- Leaving current scattered seams in place: rejected because it preserves the
  exact ambiguity and accidental coupling this change is meant to remove.

### 2. Separate provider authentication into four explicit responsibilities

Provider authentication will be modeled as four independent responsibilities:

- token acquisition
- token refresh
- token persistence
- token-to-runtime-client mapping

OpenAI remains the first implementation and the compatibility baseline for both
API-key and OAuth/subscription paths.

The seam boundary exists above provider-specific SDK details and below session
runtime code. Session actors and their persisted state continue to depend on the
provider-agnostic runtime client contract rather than storing provider-auth
internals.

Rationale:

- Prevents OAuth logic from spreading across onboarding, doctor, runtime
  startup, and provider client factories.
- Makes provider auth reviewable by responsibility.
- Allows API-key and OAuth-backed providers to share lifecycle stages without
  sharing provider-specific assumptions.

Alternatives considered:

- Keep auth logic embedded per provider factory: rejected because OpenAI already
  exercises more than one auth mode and has shown the need for sharper seams.
- Model auth as one monolithic provider-specific service: rejected because it
  hides refresh/persistence invariants and makes diagnostics less precise.

### 3. Introduce shared seam value objects at provider, channel, and notification boundaries

Free-form strings crossing generic seam boundaries will be replaced by shared
value objects such as provider identifiers, channel kinds, notification target
kinds, and module keys. These objects expose explicit primitive access only.

Rationale:

- Prevents generic code from accidentally depending on provider/channel naming
  conventions.
- Improves schema, doctor, and runtime invariant alignment.
- Matches the repository rule that value objects should not implicitly convert
  back to primitives.

Alternatives considered:

- Keep strings with helper constants: rejected because constants do not stop
  boundary drift or typo-driven runtime errors.

### 4. Generic notification flows must target abstract delivery contracts, not Slack specifics

Reminder, notification, and inbound webhook flows will depend on a generic
notification contract that identifies who should receive a message and through
which channel kind, without hardcoding Slack target kinds or Slack tool names.

The notification flow becomes:

1. producer emits a generic notification request
2. notification router resolves the target via value-object identifiers
3. channel module delivers through its compiled-in adapter

Slack remains the first delivery implementation, but generic producers do not
embed Slack assumptions.

Rationale:

- Keeps reminders and webhook-triggered flows aligned with transport-agnostic
  runtime boundaries.
- Avoids spreading Slack-only vocabulary into generic feature code.

Alternatives considered:

- Keep Slack as the notification shape and adapt later: rejected because this is
  the exact hardening window needed before more contributors build on the wrong
  seam.

### 5. Validation layers must share one invariant model and fail loudly

Schema validation, `netclaw doctor`, startup validation, and hot reload must all
agree on the same seam invariants:

- configured provider kinds must map to compiled-in provider modules
- configured channel kinds must map to compiled-in channel modules
- provider-auth configuration must satisfy the selected auth strategy
- notification targets must reference valid channel/target kinds
- partial or unknown seam definitions are invalid

The enforcement model is intentionally loud:

- schema rejects invalid structure
- doctor reports actionable seam-specific remediation
- startup blocks invalid runtime activation
- hot reload rejects invalid updates and preserves last valid state

No layer silently creates defaults, ignores unknown values, or swaps to another
provider/channel.

Rationale:

- Contributors need the same answer regardless of whether they validate via
  schema, doctor, startup, or hot reload.
- Loud failure protects security posture and prevents confusing partial runtime
  states.

Alternatives considered:

- Layer-specific validation behavior: rejected because inconsistent validators
  create contributor confusion and hide bugs.
- Silent fallback to previous or alternate modules: rejected because it hides
  invalid configuration and can change security-sensitive behavior.

### 6. Protect compatibility first with Phase 0 safety nets

Before any seam extraction, Phase 0 adds regression safety nets around the three
protected paths:

- OpenAI API-key inference path
- OpenAI OAuth/subscription path
- Slack runtime behavior

Phase 0 coverage emphasizes contract and scenario tests over many narrow local
tests. These tests become the merge gate for later phases.

Rationale:

- Reduces risk before refactors touch the most fragile cross-cutting paths.
- Produces contributor-visible confidence that the seam extraction is not
  changing external behavior accidentally.

Alternatives considered:

- Refactor first and backfill tests later: rejected because compatibility risk is
  highest at the start, not the end.

### 7. Preserve actor boundaries and minimize persistence impact

Session actors, schedule actors, and pub/sub broadcasts remain transport-
agnostic. Provider/channel/auth modules are startup and service-layer seams, not
new actor responsibilities.

Persistence implications are intentionally narrow:

- persisted session identity remains unchanged
- persisted actor events should not store provider/channel-specific primitive
  identifiers beyond existing stable contracts
- if notification/provider/channel identifiers cross persistence boundaries, use
  shared value objects with explicit serialization rules

Rationale:

- Keeps seam extraction from turning into an actor rewrite.
- Limits migration risk and review surface.

Alternatives considered:

- Push module resolution into actors: rejected because it leaks host-composition
  concerns into runtime state machines.

## Architecture Overview

The hardened seam model is:

```text
Config Files / Schema
        |
        v
Shared Validation Invariants
        |
        +--> Doctor
        +--> Startup Validation
        +--> Hot Reload Validation
        |
        v
Compiled-In Module Registries
        |
        +--> Provider Modules -----------------------------+
        |        |                                        |
        |        +--> Auth Strategy -------------------+  |
        |               |                              |  |
        |               +--> Token Acquisition         |  |
        |               +--> Token Refresh             |  |
        |               +--> Token Persistence         |  |
        |               +--> Runtime Client Mapping ---+  |
        |                                               \/
        |                                         IChatClient /
        |                                         runtime model client
        |
        +--> Channel Modules ------------------------------+
        |        |                                         |
        |        +--> Inbound adapter -> SendUserMessage   |
        |        +--> Broadcast subscriber -> delivery     |
        |                                                  |
        +--> Notification Router --------------------------+
                 |
                 +--> reminder flows
                 +--> webhook flows
                 +--> operational alerts
```

Relationship details:

- Provider modules own provider registration, model metadata access, and runtime
  client construction.
- Auth strategies are subordinate to provider modules and are selected by typed
  auth configuration, not by scattered provider-specific conditionals.
- Channel modules own inbound translation to generic runtime commands and
  outbound delivery from generic broadcasts or notifications.
- Notification routing depends on shared target value objects and channel-module
  dispatch, not Slack-specific target types.
- Generic runtime and actor code depend only on typed seam contracts and value
  objects.

## Merge-Safe Phased Milestones

### Phase 0: Compatibility safety nets

- Add contract and scenario coverage for the protected OpenAI and Slack paths.
- Capture current validation invariants and expected diagnostics for schema,
  doctor, startup, and hot reload.
- Reduce or delete low-value narrow tests that do not protect seam behavior.

Exit criteria:

- protected compatibility suites are green
- seam invariants are documented in specs and test names
- no production seam refactor required yet

### Phase 1: Shared seam types and invariant normalization

- Introduce shared value objects for provider/channel/notification boundaries.
- Normalize validation terminology and error categories across schema, doctor,
  startup, and hot reload.
- Keep OpenAI and Slack wired through existing behavior while typed seams appear
  underneath.

Exit criteria:

- seam boundary strings are reduced to config parsing and explicit conversion
- validators agree on unknown kind, missing config, and invalid auth states

### Phase 2: Provider module seam and OpenAI auth separation

- Introduce the single compiled-in provider module registry.
- Extract OpenAI into the first provider module.
- Split auth lifecycle into acquisition, refresh, persistence, and runtime
  client mapping.
- Preserve OpenAI API-key and OAuth/subscription behavior with Phase 0 tests.

Exit criteria:

- provider registration lives in one compiled-in seam
- session/runtime code consumes provider-agnostic module contracts
- protected OpenAI paths remain behaviorally identical

### Phase 3: Channel module seam and Slack compatibility preservation

- Introduce the single compiled-in channel module registry.
- Move Slack behind the channel seam without changing its Socket Mode and
  thread-bound behavior.
- Remove Slack-specific assumptions from generic runtime composition points.

Exit criteria:

- channel registration lives in one compiled-in seam
- Slack runtime scenario coverage remains green
- actor contracts remain transport-agnostic

### Phase 4: Generic notification routing

- Replace Slack-specific notification target kinds and tool names in generic
  reminder, webhook, and operational-notification flows.
- Route notifications through abstract targets resolved by channel modules.

Exit criteria:

- generic producers do not reference Slack-only target types
- Slack remains the first delivery implementation through the generic router

### Phase 5: Cleanup and contributor-facing simplification

- Remove dead compatibility branches that existed only during seam migration.
- Consolidate contract/scenario suites and contributor guidance around the new
  seam model.

Exit criteria:

- provider/channel/auth/notification seams are singular and reviewable
- contributor extension path is obvious and spec-backed

## Key Tradeoffs

- Static modules vs plugins: static modules reduce flexibility, but they keep MVP
  reviewable, deterministic, and secure-by-default.
- Early compatibility protection vs faster refactor velocity: Phase 0 adds work
  up front, but it lowers the cost of every later phase and reduces regression
  risk on the most visible paths.
- Broader scenario tests vs many local tests: broader tests can be slower and
  more setup-heavy, but they better protect cross-cutting seam behavior and make
  regressions easier to reason about.
- Shared value objects vs raw string convenience: typed seams add some ceremony,
  but they prevent boundary ambiguity and align with the repo's type-safety
  rules.

OpenAI and Slack compatibility are protected early because they are both the
highest-value existing paths and the most likely to break during seam
extraction. Preserving them first gives maintainers confidence that the new seam
architecture is an internal cleanup, not a behavioral rewrite.

## Validation And Rollout Strategy

- Land changes phase-by-phase behind merge-safe milestones rather than as one
  large refactor.
- Make Phase 0 compatibility suites required before later seam extraction merges.
- Update specs and design artifacts before implementation changes when invariants
  move.
- Keep startup and hot-reload behavior aligned by sharing validation vocabulary
  and failure categories.
- Use contributor-safe required CI that avoids live secrets, while keeping
  explicit live smoke and runtime checks opt-in.
- Treat any protected-path regression as a stop-ship issue for subsequent phases.

Recovery behavior:

- startup rejects invalid provider/channel/auth/notification seam state and does
  not boot partially
- hot reload rejects invalid changes and retains the last valid runtime state
- doctor explains the same invalid state without claiming success or applying
  hidden defaults
- optional live checks may report degraded/unreachable states, but must not
  rewrite config or silently switch modules

Rollback strategy:

- phases are designed to be revertable independently because compatibility tests
  remain stable across phases
- if a seam extraction phase regresses a protected path, revert that phase and
  keep the prior validated seam baseline

## Risks / Trade-offs

- [Provider seam extraction regresses OpenAI API-key path] -> Mitigation: Phase 0
  contract/scenario coverage lands first; do not merge Phase 2 without those
  suites green.
- [OAuth separation changes subscription behavior] -> Mitigation: make OpenAI
  the first provider-auth implementation and lock API-key and OAuth/subscription
  compatibility paths with shared scenarios and diagnostics assertions.
- [Slack abstraction leaks into actor contracts] -> Mitigation: keep channel
  seams at adapter/composition boundaries and preserve `SendUserMessage` plus
  broadcast contracts as the actor-facing interface.
- [Notification generalization reintroduces free-form string coupling] ->
  Mitigation: require shared value objects and explicit conversion at router
  boundaries.
- [Validation layers drift apart again] -> Mitigation: define one invariant model
  in spec/design and test it through schema, doctor, startup, and hot reload
  scenarios.
- [Phased migration leaves dead branches or double registration] -> Mitigation:
  each phase has explicit exit criteria and a cleanup phase removes temporary
  bridges.
- [Contributor CI becomes slower due to broader scenarios] -> Mitigation: prefer
  a smaller number of high-value scenario suites, keep live checks opt-in, and
  delete narrow tests that no longer buy confidence.
- [Future contributors ask for plugin loading prematurely] -> Mitigation:
  explicitly codify compiled-in seams as the MVP boundary in specs and design.

## Migration Plan

1. Approve the design and create the spec deltas for provider auth,
   notifications, and the modified provider/channel/testing/config capabilities.
2. Implement Phase 0 safety nets and confirm they protect OpenAI API-key,
   OpenAI OAuth/subscription, and Slack runtime behavior.
3. Introduce shared seam value objects and validation invariant alignment.
4. Extract provider module and provider-auth seams with OpenAI as the first
   module.
5. Extract channel module seam with Slack as the first channel implementation.
6. Generalize notification routing away from Slack-specific target semantics.
7. Remove transitional branches and keep only the singular seam model.

If rollout pauses mid-program, the last completed phase remains a valid merge
point because it preserves protected compatibility behavior and keeps all
validation layers fail-closed.

## Open Questions

- Should reminders and inbound webhooks share one notification-target model, or
  should they share only a lower-level delivery contract with separate producer
  metadata?
- Which existing identifiers already cross persistence boundaries and therefore
  need explicit serialization guidance when converted to value objects?
- Should provider-auth diagnostics surface lifecycle stage names directly
  (`acquisition`, `refresh`, `persistence`, `mapping`) or translate them into
  more operator-facing wording while retaining the same invariant categories?
