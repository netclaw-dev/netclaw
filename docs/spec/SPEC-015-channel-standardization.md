# SPEC-015: Channel Infrastructure Standardization

**Status:** Implemented (Phases 1-7); §1.4/§1.5 updated to the as-built API
**Goal:** Reduce per-channel implementation cost from ~15 files to ~5, and close
test coverage gaps identified in the cross-channel audit.

## Problem

Adding a new remote chat channel (Teams, WhatsApp, Signal) currently requires
implementing ~15 types across transport, actors, tools, resolvers, renderers,
and DI registration. Much of this is structural boilerplate that follows the
same pattern across all three existing channels. The test suites also have
significant coverage gaps — behaviors tested for one channel are untested for
others despite sharing the same contract.

### Current per-channel cost

| Component | Discord | Slack | Mattermost | Pattern |
|---|---|---|---|---|
| Lifecycle actor | 1,163 LOC | — (inline) | 691 LOC | Near-identical state machine |
| IChannel service | 300 LOC | 546 LOC | 302 LOC | Same connect/spawn/health shape |
| Gateway actor | 165 LOC | 165 LOC | 164 LOC | Identical routing structure |
| Conversation actor | ~250 LOC | ~250 LOC | ~260 LOC | Identical routing + ACL |
| Send message tool | 176 LOC | 124 LOC | 123 LOC | Same validate/ACL/dispatch |
| Registration extension | 113 LOC | 134 LOC | 137 LOC | Same DI wiring pattern |
| Connect failure classifier | ~50 LOC | ~50 LOC | ~50 LOC | Same classify-to-Fatal/Transient |

**Total boilerplate per channel:** ~1,200-1,800 LOC that is structurally
identical to the other channels.

### Current test coverage gaps

| Test category | Discord | Slack | Mattermost |
|---|---|---|---|
| ACL contract (19 tests) | inherited | inherited | inherited |
| Gateway routing contract (3) | inherited | **MISSING** | inherited |
| Session binding contract (~40) | inherited | inherited | inherited |
| Channel health | 5 tests | **MISSING** | **MISSING** |
| Lifecycle actor | 2 tests | N/A | 4 tests |
| Routing policy | 9 tests | 22 tests | 8 tests |
| Connect failure classifier | tested | tested | **MISSING** |
| Thread history fetcher | tested | tested | **MISSING** |
| File flow integration | tested | tested | **MISSING** |
| Message chunking | tested | **MISSING** | tested |

---

## Part 1: Infrastructure consolidation

### 1.1 Generic lifecycle actor base

**Impact:** Eliminates ~600-1,100 LOC per channel with a WebSocket transport.

Discord (1,163 LOC) and Mattermost (691 LOC) have nearly identical state
machines:

```
Disconnected → Connecting → Ready
     ↑              │          │
     │         StartFailed     │
     │              │     Disconnected/
     │              ▼     SpuriousEvent
     │         Disconnected    │
     │              ↑          ▼
     └── Disconnecting ← CleanReconnectRequired
```

Both implement the same states (`Disconnected`, `Connecting`, `Ready`,
`CleanReconnectRequired`, `Disconnecting`), the same behaviors
(`ReceiveCommon`, `ReceiveNotReadyIngress`, `ReceiveUnexpected`), the same
retry logic (exponential backoff via `ScheduleTellOnceCancelable`), and the
same shutdown pattern (`CancelRetryTimer` in `PostStop`).

**Differences that need parameterization:**

| Aspect | Discord | Mattermost |
|---|---|---|
| Transport interface | `IDiscordGatewayTransport` | `IMattermostGatewayTransport` |
| Start params | `(botToken)` | `(serverUrl, botToken)` |
| Start result | login + start + wait-for-ready | single StartAsync call |
| Ready signal | explicit Ready event from transport | implicit (StartAsync returns = ready) |
| Snapshot type | `DiscordGatewaySnapshot` | `MattermostGatewaySnapshot` |
| Event sink interface | `IDiscordGatewayEventSink` | `IMattermostGatewayEventSink` |
| Transport events | Ready, Connected, Disconnected, Log, MessageReceived, ButtonExecuted | Connected, Disconnected, LogReceived, MessageReceived |

**Design approach:** Extract a `ChannelLifecycleActor<TSnapshot>` base class
in `Netclaw.Channels` that implements the state machine, retry logic, and
health reporting. Per-channel subclasses provide:

- A `StartTransportAsync()` / `StopTransportAsync()` template method pair
- A `CreateSnapshot()` factory
- Transport event subscription/unsubscription in `Subscribe()` / `Unsubscribe()`
- Message forwarding in `HandleIngressMessage()`

The base handles: state transitions, `Become()` calls, retry scheduling,
`PostStop` cleanup, `GetSnapshot` handling, `Connect`/`Disconnect` protocol,
and `CleanReconnectRequired` → disconnect → auto-reconnect flow.

**Prerequisite:** Unify the snapshot types. Both `DiscordGatewaySnapshot` and
`MattermostGatewaySnapshot` carry `(IsConnected, IsReady, HealthDetail)` plus
a platform-specific bot identity. Extract a `GatewaySnapshot` base or
interface:

```csharp
public interface IGatewaySnapshot
{
    bool IsConnected { get; }
    bool IsReady { get; }
    string? HealthDetail { get; }
}
```

### 1.2 Standardized gateway client interface

**Impact:** Enables the generic lifecycle actor and simplifies `IChannel` implementations.

Currently each channel has its own gateway client interface
(`IDiscordGatewayClient`, `IMattermostGatewayClient`). The common surface is:

```csharp
public interface IGatewayClient<TSnapshot> where TSnapshot : IGatewaySnapshot
{
    event Func<string, Task> CleanReconnectRequired;
    event Func<TSnapshot, Task> ConnectionRestored;

    Task<TSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
}
```

Platform-specific connect methods and message events stay on the concrete
interfaces. The common surface enables the `IChannel` health-reporting and
shutdown logic to be shared.

### 1.3 Generic gateway + conversation actor pair

**Impact:** Eliminates ~350 LOC per channel.

All three gateway actors (165 LOC each) do exactly the same thing:

1. Receive normalized inbound messages
2. Extract a routing key (channel ID / guild ID)
3. Get-or-create a child conversation actor for that routing key
4. Forward the message to the child
5. Handle `ReceiveTimeout` for passivation
6. Handle `Terminated` for child cleanup

The conversation actors (~250 LOC each) similarly:

1. Receive messages from the gateway parent
2. Extract a session ID (thread ID)
3. Get-or-create a session binding actor for that session ID
4. Forward, with ACL checks and dedup
5. Handle proactive thread creation
6. Handle `ReceiveTimeout` for passivation

**Design approach:** A generic `ChannelGatewayActor<TMessage>` that takes:

- A `Func<TMessage, RoutingKey> extractRoutingKey` for the gateway level
- A `Func<TMessage, SessionId> extractSessionId` for the conversation level
- A `Props` factory for creating the session binding actor
- An `IAclPolicy<TMessage>` for ACL checks

Each channel provides its message type and the extraction/factory functions.
The actor hierarchy structure, passivation, child management, and dedup are
all generic.

**Risk:** Slack has a slightly different gateway model (it receives
`SlackInboundMessage` which wraps SlackNet events, and it handles both
`MessageEvent` and `AppMention` as separate paths). Need to verify the
generic model can accommodate this without becoming more complex than the
concrete implementations.

### 1.4 Registration builder

**Impact:** Eliminates ~100 LOC of boilerplate per channel, makes the
registration pattern discoverable.

Replace per-channel `AddXxxChannelIntegration` methods with a fluent builder:

As implemented (`RemoteChatChannelBuilder` in `Netclaw.Daemon`): the channel
type and any additional output effects are parameters of `AddRemoteChatChannel`
(so the descriptor registers eagerly even when the channel is disabled), and
the `With*` methods take factories:

```csharp
services.AddRemoteChatChannel<MattermostChannel, MattermostChannelOptions>(
        ChannelType.Mattermost, configuration)
    .WithTransport<IMattermostGatewayClient>((sp, options) => ...)
    .WithReplyClient<IMattermostReplyClient>((sp, options) => ...)
    .WithProactiveSendClient((sp, options, channel) => ...)   // IChannelOutboundClient
    .WithResolver((sp, options) => ...)                       // IChannelAddressResolver
    .WithRenderer((sp, options) => ...)
    .WithReminderResolver((sp, options) => ...)
    .WithThreadHistory((sp, options) => ...)
    .WithLookupTool((sp, options) => ...)
    .WithServices((services, options) => ...);                // one escape hatch per channel for SDK quirks
```

The builder handles:
- Options binding from `IConfiguration`
- Descriptor creation via `CreateRemoteChat`
- Keyed `IChannel` + `IHostedService` registration
- `IChannelTool` marker registration
- HTTP client creation with `AddNetclawHeaders`
- Skipping implementation registrations when `!options.Enabled`
- Always registering the descriptor (even when disabled)

**Risk:** Low. The builder is purely DI sugar — no behavioral change. Can be
introduced incrementally (one channel at a time migrates to the builder while
the others keep their manual extensions).

### 1.5 Per-channel send tool elimination

**Impact:** Eliminates ~125-175 LOC per channel.

`SendSlackMessageTool`, `SendDiscordMessageTool`, and
`SendMattermostMessageTool` all follow the same pattern:

1. Parse and validate `channel_id`/`user_id` parameters
2. ACL-check the destination
3. Call the outbound client to post

`SendChannelMessageTool` already dispatches by `channel_key`. If we
standardize the outbound interface:

```csharp
public interface IChannelOutboundClient
{
    ChannelDescriptorKey Key { get; }
    Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct);
}
```

`SendChannelMessageTool` dispatches directly to the outbound client by key,
and the per-channel send tools were deleted entirely (Phase 7, implemented).
The risk noted below turned out to be moot: since the registration builder
landed, the per-channel send tools were never `IChannelTool`-marked — they were
consumed only by `SendChannelMessageTool`'s internal switch, so their
channel-specific parameters (`thread_name`, default-channel fallback) were
unreachable dead surface. The per-channel logic now lives in
`SlackProactiveOutboundClient` / `DiscordProactiveOutboundClient` /
`MattermostProactiveOutboundClient`, which sit ABOVE the SDK transport
interfaces because the ACL checks must live above the fake seam used in tests.

### Summary: what stays per-channel

After all consolidation, the irreducible per-channel surface would be:

| Component | Purpose | Estimated LOC |
|---|---|---|
| `XxxChannelOptions` | Config binding | ~30 |
| `XxxGatewayTransport` | SDK adapter (the real work) | 150-300 |
| `XxxReplyClient` | Replies in existing threads | ~50-100 |
| `XxxOutboundClient` | Proactive posting | ~50-100 |
| `XxxAddressResolver` | Platform-specific ID resolution | ~100-200 |
| `XxxConnectFailureClassifier` | SDK exception classification | ~50 |
| Builder call in Program.cs | DI wiring | ~15 |

**Per-channel cost: ~450-800 LOC** (down from ~1,500-2,000).

Types eliminated per channel: lifecycle actor, IChannel service (uses generic
base), gateway actor, conversation actor, send message tool, registration
extension.

---

## Part 2: Test consolidation and gap closure

### 2.1 New contract test bases

#### `ChannelHealthContractTests` (new)

**Closes gap for:** Slack, Mattermost (currently Discord-only, 5 tests)

Tests the `IChannel.GetHealthAsync()` contract:
- Healthy when transport is connected and ready
- Degraded when connected but not ready
- Disconnected when transport is disconnected
- Degraded when channel is disabled
- Health detail propagated from transport snapshot

Each channel provides a fixture with its `IChannel` + a controllable fake
transport.

#### `GatewayLifecycleContractTests` (new)

**Closes gap for:** ensures both Discord and Mattermost (and future channels)
cover the same state machine behaviors.

Tests (the union of what Discord and Mattermost currently cover):
- Not-ready ingress is dropped
- Runtime disconnect reports not-ready and requests clean reconnect
- Spurious connected/ready event while disconnected triggers clean reconnect
- Reconnect cycle does not duplicate transport handlers
- Clean reconnect state reports not-ready even when transport remains connected
- Auto-reconnect fires after disconnect
- Timer cancelled on actor stop

Each channel provides a fixture with its lifecycle actor + fake transport.

#### `RoutingPolicyContractTests` (new)

**Extracts from:** `DiscordRoutingPolicyTests` (9), `SlackRoutingPolicyTests`
(22), `MattermostRoutingPolicyTests` (8)

Shared core tests (~8):
- Message without mention ignored when mention-only mode
- Existing thread continues without mention
- Thread reply rehydrates session when no actor exists
- DM routing matrix (4 scenarios: allow×mention combinations)
- Empty content ignored

Platform-specific tests stay in standalone files:
- Slack: file_share subtypes, hidden messages, bot_message filtering,
  BlockAction refusal (~14 tests)
- Discord: interaction routing
- Mattermost: top-level message filtering

### 2.2 Missing Slack gateway routing contract

`SlackGatewayContractTests` is missing from the contracts directory. Discord
and Mattermost both have their gateway routing contract implementations. Add
`SlackGatewayContractTests` inheriting from `GatewayRoutingContractTests` to
close this gap.

### 2.3 Mattermost test coverage gaps

| Gap | Action | Priority |
|---|---|---|
| Connect failure classifier | Add `MattermostConnectFailureClassifierTests` | High — classifier exists, just untested |
| Thread history fetcher | Add `MattermostThreadHistoryFetcherTests` | Medium — if Mattermost supports threaded history |
| File flow integration | Add `MattermostFileFlowIntegrationTests` | Medium — Mattermost supports attachments |
| Channel health | Covered by new `ChannelHealthContractTests` | Part of 2.1 |

### 2.4 Slack test coverage gaps

| Gap | Action | Priority |
|---|---|---|
| Message chunking | Investigate whether Slack has chunking logic; add tests if so | Low — Slack may use Block Kit instead |
| Gateway routing contract | Add `SlackGatewayContractTests` | High — simple, just wire the base |
| Channel health | Covered by new `ChannelHealthContractTests` | Part of 2.1 |

---

## Part 3: Implementation order

Work is organized into phases that can be shipped independently. Each phase
is a PR-sized unit.

### Phase 1: Test gap closure (no production code changes)

Ship missing tests against the existing channel implementations. This
establishes the coverage baseline before any refactoring.

1. Add `SlackGatewayContractTests` (missing contract implementation)
2. Add `MattermostConnectFailureClassifierTests`
3. Add `MattermostThreadHistoryFetcherTests` (if applicable)
4. Add `MattermostFileFlowIntegrationTests` (if applicable)

### Phase 2: New contract test bases

Extract shared test behaviors into abstract bases. Existing standalone tests
are replaced by inheriting implementations.

1. `ChannelHealthContractTests` + per-channel fixtures (Discord, Slack, Mattermost)
2. `GatewayLifecycleContractTests` + per-channel fixtures (Discord, Mattermost)
3. `RoutingPolicyContractTests` + per-channel fixtures (all three)

### Phase 3: Gateway snapshot interface

Small production change that unblocks the generic lifecycle actor.

1. Define `IGatewaySnapshot` in `Netclaw.Channels`
2. Have `DiscordGatewaySnapshot` and `MattermostGatewaySnapshot` implement it
3. Slim down `IChannel` health reporting to use the common interface

### Phase 4: Generic lifecycle actor

The biggest single win.

1. Extract `ChannelLifecycleActor<TSnapshot>` base in `Netclaw.Channels`
2. Migrate `DiscordNetGatewayLifecycleActor` to inherit from the base
3. Migrate `MattermostNetGatewayLifecycleActor` to inherit from the base
4. Verify contract tests still pass
5. Delete duplicated state machine code from both

### Phase 5: Registration builder

Pure DI sugar, no behavioral change.

1. Implement `RemoteChatChannelBuilder<TChannel, TOptions>` in `Netclaw.Daemon`
2. Migrate one channel (Mattermost — simplest) to the builder
3. Migrate Discord
4. Migrate Slack
5. Delete old registration extension classes

### Phase 6: Generic gateway + conversation actors

1. Extract `ChannelGatewayActor<TMessage>` in `Netclaw.Channels`
2. Extract `ChannelConversationActor<TMessage>` in `Netclaw.Channels`
3. Migrate channels one at a time
4. Verify gateway routing contract tests still pass

### Phase 7: Send tool consolidation

1. Define `IChannelOutboundClient` common interface
2. Migrate outbound clients to implement it
3. Route `SendChannelMessageTool` through the common interface
4. Remove per-channel send tools (or reduce to thin wrappers for
   channel-specific parameters)

---

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Generic base becomes a leaky abstraction that's harder to debug than concrete impls | Each phase ships independently. If a generic base makes things worse, revert that phase without affecting others. |
| Slack's gateway model doesn't fit the generic gateway actor pattern | Investigate in Phase 6 before committing. Slack can stay concrete if the generic model doesn't fit. |
| Per-channel send tool parameters don't fit a common schema | Phase 7 can stop at a shared base class instead of full elimination. |
| Contract test fixtures become complex enough to defeat the purpose | Keep fixtures minimal — if a fixture is >100 LOC, the contract is too broad. |

## Success criteria

- A new remote chat channel can be added with ≤5 production files (options,
  transport, reply client, outbound client, address resolver) plus the
  builder call
- All three existing channels pass the same contract test suites for: ACL,
  gateway routing, session binding, channel health, lifecycle, and routing
  policy
- No test coverage regression — every behavior tested today is still tested
  after consolidation
