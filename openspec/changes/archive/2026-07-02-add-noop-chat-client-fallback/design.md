## Context

Netclaw routes all model access through the Microsoft.Extensions.AI
`IChatClient` abstraction. The current composition root in
`Netclaw.Daemon.Configuration` builds a `NetclawChatClientProvider` from a
`ProviderPluginFactory` + `ModelSelection` at host construction time. If
provider configuration is absent or invalid, factory construction throws and
the daemon fails to come up. Operators are then left without the surfaces
(`netclaw doctor`, `netclaw model`, the running daemon's diagnostics) that
would help them fix the problem.

Onboarding (`netclaw-onboarding`) already differentiates "exposure validation
failure" from generic readiness timeouts so the wizard can show actionable
errors — but a missing/invalid inference provider is still treated as a fatal
startup error rather than a recoverable, surfaced state.

This change introduces a fallback path: a No-Op `IChatClient` is registered
whenever validation reports "no valid provider configuration." Startup
succeeds; chat turns return a fixed, instructional message; doctor flags the
state clearly. The real client comes back on restart once configuration is
fixed.

## Goals / Non-Goals

**Goals:**

- Daemon SHALL start successfully when no valid provider/model configuration
  is present.
- Any actor that requests an `IChatClient` via `IChatClientProvider` SHALL
  receive a deterministic No-Op client whose responses are a fixed
  configuration-error message with actionable recovery steps.
- The No-Op response SHALL reference available provider options when the
  config layer can enumerate them (cached/known provider list).
- `netclaw doctor` SHALL report the degraded "no-op chat client active"
  state distinctly from other failure modes.
- All downstream code paths (sessions, sub-agents, memory curation, title
  generation, compaction) keep working against `IChatClient` without
  special-casing the No-Op variant.

**Non-Goals:**

- **No hot-swap.** Picking up a newly-valid configuration requires a daemon
  restart. Live swapping the chat client mid-process is out of scope and
  would interact poorly with in-flight session pipelines and Akka actor
  lifecycles. Follow-up tracked under `netclaw-config-hot-reload`.
- **No partial-validity heuristics.** If validation cannot prove the
  configuration is valid (provider+model present, required credentials
  present, schema valid), the No-Op client is selected. We do not try to
  "use the provider but skip the model" or similar half-states.
- **No tool calls or external I/O from the No-Op client.** It is intentionally
  inert.
- **No auto-`doctor --fix` on startup.** Operator runs `netclaw doctor`
  explicitly.

## Decisions

### Decision 1: A new "no provider configured" validation outcome, distinct from "invalid"

Provider/model configuration validation currently has a binary outcome from
the daemon's perspective: either the `ProviderPluginFactory` can build clients,
or startup fails. We introduce a third outcome:

- **valid** — build real clients (today's happy path).
- **no provider configured** — non-fatal; select No-Op client.
- **invalid** — fatal; surface validation error (today's failure path
  preserved for *malformed* config, e.g. schema violations, bad credentials
  for a configured provider).

**Why split these:** the failure modes have different operator remediation.
"No provider configured" is the first-run / not-yet-onboarded shape and the
operator's next step is `netclaw model`. "Invalid" means the operator
attempted a config and something is malformed — they need the specific
validation error, not a generic "please configure a model" message. Collapsing
these would either swallow real configuration bugs behind the No-Op fallback
or force every first-run user to hit a fatal error before discovering
`netclaw model`. The split aligns with the
[fail-loudly principle in CLAUDE.md][rule] — we only fall back when partial
failure is a normal runtime condition (here: a fresh install without a model).

[rule]: see "No silent fallbacks" under Universal Quality Bar.

**Alternative considered:** always select No-Op when client construction
throws. Rejected — it would hide misconfiguration bugs (bad URL, wrong
credentials, schema-rejected fields) behind a generic message and violate the
no-silent-fallbacks rule.

### Decision 2: Register `NoOpChatClient` as `IChatClient`, behind the same `IChatClientProvider` contract

`NetclawChatClientProvider` is the choke point all actors go through to
acquire chat clients. We add a sibling provider implementation
`NoOpChatClientProvider` (or a static factory branch inside the existing
provider) that returns the same `NoOpChatClient` instance for every
`ModelRole`. Composition root picks one or the other based on the validation
outcome above.

**Why at the provider level, not inside the factory:** the factory builds
clients from `ModelSelection`; selecting "no provider" earlier means we never
try to build, we never instantiate provider plugins, and we keep
provider-specific failure paths out of the degraded code path. It also keeps
the No-Op client transport-agnostic — it does not pretend to be any provider.

**Alternative considered:** make the existing `NetclawChatClientProvider`
tolerate a null `ModelSelection.Main` and return No-Op internally. Rejected
because it muddles the contract — callers that successfully constructed the
provider should be able to assume they have a working client surface.
Branching at composition keeps each provider implementation honest.

### Decision 3: `NoOpChatClient` returns a single deterministic message

Response contract (same for streaming and non-streaming):

```
No valid model configuration detected.

Netclaw is running, but no inference provider/model is configured. To get
chat working:

  1. Run `netclaw doctor` to see what's missing.
  2. Run `netclaw model` to pick a provider and model interactively.
  3. Or edit `netclaw.json` directly and restart the daemon.

Available providers: <comma-separated list>     (when discoverable)
```

The "Available providers" line is appended **only** when the configuration
layer can enumerate known provider profiles without contacting any
external service. We do not probe networks from the No-Op client.

For streaming responses, the No-Op client emits the message as a single
`ChatResponseUpdate` chunk plus a completed signal — no artificial
token-by-token streaming. The downstream session pipelines do not depend on
specific chunking behavior.

Tool-calling: the No-Op client SHALL NOT emit tool calls regardless of the
tools registered on the request. This is enforced unconditionally so the
secure-by-default tool surface is preserved.

### Decision 4: Doctor surfaces "no-op chat client active" as a distinct health item

`netclaw doctor` SHALL include a check that reports:

- **pass** — a real provider client is active.
- **warn** — the No-Op client is active because no valid provider
  configuration was detected. The message includes the next-step commands.
- **fail** — provider configuration was malformed (delegates to the existing
  validation-failure message).

This separates "degraded but recoverable" from "broken" in operator
diagnostics.

### Decision 5: Startup logs the fallback selection once, at WARN

When the No-Op provider is selected, the host SHALL log a single WARN-level
entry naming the selection reason and pointing at `netclaw doctor`. We
explicitly do **not** repeat the warning on every chat turn — the No-Op
client's response is itself the per-turn signal to the user.

## Risks / Trade-offs

- **[Risk] Operators mistake the No-Op message for a model response.**
  → Mitigation: the message leads with the exact phrase `"No valid model
  configuration detected."` and `doctor` flags the state. Slack-side
  presentation is unchanged (no special formatting), but the wording is
  unambiguous.

- **[Risk] Hiding genuine misconfiguration bugs under the No-Op fallback.**
  → Mitigation: Decision 1 — only the "no provider configured" outcome
  selects No-Op. Malformed config still fails startup loudly.

- **[Risk] Tool calls from a session against the No-Op client could leak
  intent or fire side-effects if the No-Op accidentally returned tool calls.**
  → Mitigation: Decision 3 — `NoOpChatClient` unconditionally returns no
  tool calls. Tested.

- **[Risk] Memory/compaction/sub-agent pipelines could persist No-Op
  responses as if they were real model output, polluting recall later.**
  → Mitigation: callers receive a normal `ChatResponse`, but the content is
  deterministic and obviously a configuration banner. Memory curation will
  see the same banner repeatedly and treat it as low-signal. We do not add
  a separate "is-noop" flag to `ChatResponse` (would leak the abstraction);
  callers that genuinely need to know can detect the situation by asking the
  `IChatClientProvider` whether it is the no-op variant — see open question.

- **[Trade-off] Restart-required to recover.** Operators must restart the
  daemon after fixing config. Acceptable for MVP given the actor-system
  lifecycle complexity of hot-swapping `IChatClient` instances; hot-reload
  belongs to its own change.

- **[Risk] Actors that special-case errors (e.g., `SessionLlmInvoker`'s
  retry/failover logic) might treat a No-Op response as a successful call
  and skip onto downstream behavior.** → Mitigation: this is the *intended*
  behavior — the call did succeed, the model just returned a configuration
  banner. No retry storm, no failover. Verified explicitly in tests against
  `ResilientChatClientProviderDecorator` and `FailoverChatClient`.

## Migration Plan

- Pure addition; no existing config schema changes.
- Existing deployments with valid provider config behave identically (path
  is unchanged).
- Deployments that *previously* failed startup due to missing provider
  config will now come up in degraded mode. Documented in the release notes
  and `netclaw doctor` output. No rollback procedure required beyond
  reverting the daemon binary.

## Resolved Decisions (formerly open questions)

- **`IChatClientProvider.IsDegraded`** — added as a default interface method
  returning `false`. `NoOpChatClientProvider` overrides to `true`. Existing
  test doubles and `SingleClientProvider` inherit the default and need no
  changes.
- **`chat.noop_responses_total` metric** — **deferred**. The doctor warn
  item and the per-turn banner are sufficient signals for operator
  awareness in MVP; a metric is easy to add later if production telemetry
  reveals operators silently running in degraded mode. Tracked as a
  follow-up, not blocking this change.
- **Banner copy** — finalized at implementation time; structure matches
  this design. See `NoOpChatClient.BuildBanner` for the canonical text.
  Eval suite is unchanged because the banner only surfaces in degraded
  mode, which the eval suite does not cover.
