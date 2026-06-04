## Context

Netclaw ships Slack and Discord channels behind a transport-agnostic adapter
boundary (`PRD-009`). Both channels use a three-tier Akka.NET actor hierarchy
(Gateway → Conversation → ThreadBinding/SessionBinding), normalize inbound
events into `ChannelInput` with explicit trust context, gate dispatch on a
default-deny ACL, and deliver replies into the originating thread.

Mattermost has no integration. A prior attempt (PR #877) is now ~111 commits
behind `dev`: it predates the channel conformance contract bases, the
value-object refactor (`ModelId`, `TurnNumber`), and roughly a dozen per-channel
bug fixes hardened into Slack and Discord. Its actor/policy/history code was
copied from the pre-fix Slack/Discord patterns, so it reproduces every one of
those resolved defects.

This change adds Mattermost with full Slack/Discord parity plus three
behaviors the operator explicitly requires: scheduled-reminder delivery to
direct messages, thread-history backfill, and reminder-spawned interactive
sessions. Stakeholders are owner-operators running Netclaw in Mattermost-first
environments and maintainers responsible for fail-closed security, deterministic
routing, and CI-safe validation.

Mattermost differs from Slack and Discord in one architecturally significant
way: interactive message button clicks are delivered **only** over an inbound
HTTP POST to an integration URL. Slack Socket Mode and the Discord gateway both
carry interaction responses over their existing outbound WebSocket, so neither
needs an inbound HTTP surface. Mattermost interactive approvals therefore
require a new authenticated inbound endpoint.

## Goals / Non-Goals

**Goals:**

1. Add a Mattermost channel that reaches Slack/Discord parity for gateway
   lifecycle, ACL-gated ingress normalization, deterministic thread-aware
   session identity, and thread-bound reply delivery.
2. Implement thread-history backfill that hydrates bot-authored messages only at
   the thread root and re-arms deferred hydration on the first authorized
   inbound.
3. Implement proactive sends with an acknowledged thread-initialization
   handshake.
4. Support scheduled-reminder delivery to both channels and direct messages, and
   reminder-spawned interactive sessions.
5. Support interactive approvals via a channel-owned, token-authenticated,
   ACL-checked inbound HTTP callback endpoint, with a deterministic text-reply
   fallback.
6. Preserve transport-agnostic actor boundaries, fail-closed startup, and
   default-deny ACL.
7. Provide CI-safe offline conformance coverage and optional Testcontainers
   integration coverage.

**Non-Goals:**

- Mattermost plugin packaging or server-side slash-command app registration.
- Voice/call features, role administration, or moderation automation.
- Cross-channel session merging across Slack/Discord/Mattermost.
- Any change to session actor or persistence contracts.

## Decisions

### D1. Rewrite on a fresh branch; salvage Mattermost-specific assets from PR #877

**Choice:** Do not rebase PR #877. Build the actor, policy, history-fetcher, and
contract-test layer fresh by mirroring the *current* Slack and Discord
implementations. Port from #877 only the pattern-agnostic Mattermost-specific
research: the Mattermost.NET library integration and transport client layer, the
Testcontainers harness, channel constants (16,383-char message limit, `@username`
mention format, file-detail resolution), the callback-endpoint shape, and the
config-schema design.

**Why:** #877's actor/policy/history code was copied from pre-fix Slack/Discord
patterns and reproduces ~12 resolved bugs. Mirroring current code inherits every
fix by construction; auditing old code against a fix list is error-prone and one
miss ships a known defect.

**Alternative considered:** rebase the #877 branch. Rejected — starts from
known-bad code and requires hand-porting a value-object refactor plus the fix
set into the Mattermost actors.

### D2. Three-tier actor hierarchy mirroring Slack

**Choice:** `MattermostGatewayActor` (event dedup, ACL at gateway) →
`MattermostConversationActor` (per-channel routing) → `MattermostThreadBindingActor`
(persistent, per-thread, session-scoped). The channel itself
(`MattermostChannel`) is an `IChannel`/`IHostedService` that owns the WebSocket
lifecycle.

**Why:** Keeps Mattermost behavior consistent with Slack/Discord and reuses the
shared conformance contract suites. Session actors stay transport-agnostic.

**Alternative considered:** flat single-actor design. Rejected — duplicates
routing logic and cannot satisfy the gateway/binding contract suites.

### D3. Interactive approvals via a channel-owned token-authenticated callback endpoint

**Choice:** Register `/api/mattermost/actions` as a channel-owned ASP.NET route
alongside the daemon's other channel handlers. Each button option is minted with
a **single-use opaque action token** (32 bytes RNG, channel-bound, 12h TTL,
server-stored in `MattermostCallbackActionStore`) that the Mattermost server
echoes back inside the action's `context` field. On callback the daemon
consumes the token from the store; consumption is atomic so a replayed click
returns "no longer valid." The resolved sender is then ACL-checked, the payload
channel must match the token's channel, and the call is routed by session
identity to the owning session actor — mirroring the security envelope
`SlackApprovalHandler` enforces over Socket Mode. A deterministic text-reply
fallback (A/B/C/D) is always available and arrives over the WebSocket like any
message.

**Why:** Mattermost delivers button clicks only via inbound HTTP. The existing
inbound-webhook system cannot host this: it is one-way ingestion that spawns a
fresh autonomous session per delivery and has no path to route a response into
an existing session's pending-approval state. A channel-owned endpoint is the
correct, security-scoped surface. Opaque single-use tokens (rather than HMAC
over the payload) give equivalent forge-rejection on this path while keeping
the secret entirely server-side — the Mattermost server never sees or stores a
signing key, and a leaked token is single-use and channel-scoped.

**Alternative considered:** HMAC-signed payload with a per-daemon ephemeral
key. Rejected — would require embedding the key (or a key id) in the button
context that ships through the Mattermost server, with no functional benefit
over server-side opaque-token storage; opaque tokens also let the daemon
invalidate outstanding actions per-call at no extra cost (e.g. on requester
change). Considered reusing the inbound-webhook system. Rejected —
architecturally one-way; no bidirectional routing into live sessions.
Considered text-only approvals for MVP; rejected because it would leave
Mattermost below Slack/Discord parity, though it remains the documented
fallback if the inbound HTTP surface is later deemed undesirable.

### D4. Thread-history backfill: root-only bot dedup, cursor as cost optimization

**Choice:** `MattermostThreadHistoryFetcher : IThreadHistoryFetcher` hydrates
bot-authored messages **only** when the message is the thread root; all
bot-authored messages below the root are excluded. The watermark cursor is a
cost optimization, not the dedup primitive. Deferred one-shot hydration re-arms
and completes on the first authorized inbound.

**Why:** Reproduces the resolved Slack/Discord behavior (fixes `786b5985`,
`45f4c57b`, `d806f81f`). Including bot messages below root re-adopts the agent's
own output as external context; relying on the cursor for dedup lagged under
backfill.

**Alternative considered:** cursor-based dedup (the #877 approach). Rejected as
the exact pattern those fixes removed.

### D5. Scheduled-reminder DM delivery is supported

**Choice:** `MattermostReminderTargetResolver` accepts `@<userId>` for direct
messages and `channel:<channelId>` for channel posts, and canonicalizes both
forms with their prefix preserved (`@<userId>` / `channel:<channelId>`) so the
downstream reminder prompt is unambiguous — Mattermost user IDs and channel
IDs are both 26-char alphanumeric strings, so a bare ID downstream would not
distinguish the two dispatch paths. Bare ambiguous IDs are rejected.

**Why:** Mattermost direct messages are addressable channels with stable IDs, so
DM delivery is well-defined — unlike Discord, whose resolver rejects DM targets
because Discord lacks a stable DM session model. This is a deliberate parity
improvement.

**Alternative considered:** mirror Discord and reject DM targets. Rejected — the
operator explicitly requires scheduled DM sends, and Mattermost's model supports
them cleanly.

### D6. Connection-failure containment and fail-closed startup

**Choice:** A missing or invalid Mattermost token is treated as a *Fatal
connection failure handled gracefully* — token validity is checked in
`StartAsync`, never thrown from DI registration. `MattermostConnectFailureClassifier`
splits failures into Fatal vs Transient; on Fatal codes the WebSocket client is
stopped to avoid retry spam; the channel degrades in isolation and never aborts
the daemon. Transient failures drive a bounded backoff reconnect loop.

**Why:** Reproduces the resolved Slack/Discord behavior (fixes `07cdbb22`,
`97c4e9a6`, `e222be52`). Throwing from DI registration aborted host
construction before any channel could start.

**Alternative considered:** validate the token at registration. Rejected — one
misconfigured channel must not take down the daemon or other channels.

### D7. Approval state lives on the session actor, routed by SessionId

**Choice:** Pending-approval state is held by the session actor, not the
`MattermostThreadBindingActor`. Approval responses (button callback or text
reply) route by SessionId; passivated children are lazy-spawned. The binding is
pure transport.

**Why:** Reproduces fix `00034827`. State on a passivatable binding was lost on
re-spawn, silently dropping approval responses.

**Alternative considered:** keep pending state on the binding. Rejected — the
exact defect that fix removed.

### D8. CI stays provider/channel independent; integration tests are opt-in

**Choice:** Required CI covers Mattermost via the shared conformance contract
suites (`MattermostAclContractTests`, `MattermostGatewayContractTests`,
`MattermostSessionBindingContractTests`), unit tests, and deterministic fakes —
no live Mattermost. A separate `Netclaw.Channels.Mattermost.IntegrationTests`
project uses Testcontainers against a real Mattermost server and is not part of
required CI.

**Why:** Preserves the existing CI principle that required suites do not depend
on external live systems.

**Alternative considered:** gated live Mattermost smoke tests in required CI.
Rejected — flaky and credential/network dependent.

### D9. Use the Mattermost.NET client library

**Choice:** Depend on the Mattermost.NET client library for WebSocket events and
REST operations (the library #877 selected), pinned in `Directory.Packages.props`
after confirming the latest maintained version.

**Why:** Avoids hand-rolling the Mattermost v4 API surface; #877 already proved
the integration.

**Alternative considered:** hand-rolled HTTP/WebSocket client. Rejected — high
maintenance cost for no parity benefit.

## Risks / Trade-offs

- **New inbound HTTP attack surface** → endpoint authenticates each callback
  by single-use opaque action token (32 bytes RNG, server-stored, channel-bound,
  12h TTL) and re-runs the ACL on the resolved sender before any state change.
  Fails closed on invalid config, and is only registered when the Mattermost
  channel is enabled with interactive approvals configured. Text fallback works
  with the endpoint disabled.
- **Callback token lifecycle** → tokens live only in process memory and are
  consumed on first hit. A daemon restart invalidates every outstanding action,
  bounding replay exposure to the daemon lifetime. Documented in the runbook.
- **Mattermost thread/DM semantics differ from Slack/Discord** → explicit
  entity-key derivation (`{channelId}/{rootPostId}`) and parity tests for
  threaded, non-threaded, and DM cases.
- **Reintroducing a resolved channel bug** → the design pins each fix (D4, D6,
  D7) to its origin commit; conformance contract suites enforce the behavior.
- **Mattermost.NET API drift** → isolate all library calls behind the
  `Transport/` client layer so an upgrade or replacement is contained.
- **Adapter complexity** → channel-specific behavior stays in
  `Netclaw.Channels.Mattermost`; session/persistence contracts are untouched.

## Migration Plan

1. Land the OpenSpec deltas for `netclaw-mattermost-socket` and the modified
   capabilities.
2. Add the `Netclaw.Channels.Mattermost` project (transport, actors, policies,
   tools, approval handler) and wire it into the daemon.
3. Add the config-schema entry with defaults so `netclaw doctor --fix` migrates
   pre-Mattermost configs cleanly.
4. Register the `/api/mattermost/actions` callback route, gated on channel
   enablement.
5. Add `Netclaw.Channels.Mattermost.IntegrationTests` (Testcontainers).
6. Add Mattermost conformance contract subclasses, unit tests, and proactive /
   backfill integration tests.
7. Validate via `openspec validate`, `dotnet test`, and `dotnet slopwatch
   analyze`.

Rollback: disable the Mattermost channel in config and remove runtime wiring.
No persistence migration is needed — session IDs remain string-based and
transport-agnostic; no Slack/Discord behavior changes.

## Failure modes and recovery behavior

- **Missing/invalid Mattermost token:** classified Fatal at `StartAsync`; the
  channel reports degraded health; the daemon and other channels keep running.
- **Mattermost WebSocket disconnect:** Transient classification drives a bounded
  backoff reconnect; session identity continuity is preserved.
- **Fatal gateway close code:** the WebSocket client is stopped to prevent retry
  spam; health reports disconnected.
- **Callback endpoint receives an unsigned/forged payload:** request is rejected
  before any session routing; no approval state changes.
- **Callback endpoint unreachable:** approvals proceed via the deterministic
  text-reply fallback.
- **ACL denial:** inbound event or callback is rejected pre-dispatch with a
  structured deny reason.
- **Duplicate reminder fire:** an in-flight reminder execution is tracked; a
  second fire is acknowledged and dropped without parallel execution.

## Open Questions

None blocking. The text-only-approval fallback (D3) remains available as a
documented scope reduction if the inbound HTTP surface is later deemed
undesirable for a given deployment.
