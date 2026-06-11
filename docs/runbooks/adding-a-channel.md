# Adding a New Channel

This runbook walks through adding a new remote chat channel integration to
Netclaw (e.g., Microsoft Teams, WhatsApp, Signal) on the post-SPEC-015
architecture. All three existing channels — Slack, Discord, Mattermost —
are built on the generic bases in `src/Netclaw.Channels/` and registered
through one fluent builder chain, so a new channel writes only the
platform-specific surface: an options class, an SDK transport adapter, a
reply client, a proactive outbound client, and an address resolver, plus
thin actor subclasses over the shared bases.

**Recommended starting point:** Mattermost (`src/Netclaw.Channels.Mattermost/`)
is the simplest and most canonical reference — clone it and adapt. Its
registration chain in
`src/Netclaw.Daemon/Configuration/ChannelIntegrationRegistrationExtensions.cs`
is the cleanest example of the builder API.

> **Background reading:** `docs/spec/SPEC-015-channel-standardization.md`
> documents the consolidation design, what is generic vs. per-channel, and
> why. This runbook is the operational walkthrough of that design.

## Architecture overview

The generic infrastructure lives in two places:

- **`src/Netclaw.Channels/`** — the actor bases and delivery contracts:
  - `ChannelLifecycleActor<TSnapshot, TConnectCommand>` — gateway connection
    state machine (reconnect, backoff, health snapshots)
  - `ChannelGatewayActor<TChannelId>` — inbound routing to per-conversation
    children, duplicate-event tracking, and `SessionIdFormat` (the single
    owner of the session-id grammar)
  - `ChannelConversationActor<TMessage>` — the per-conversation security
    pipeline (ACL → bot filter → ingress gate → routing policy → text
    normalization → session binding)
  - `ChannelDeliveryContracts.cs` — `ChannelDescriptor`, `ChannelRegistry`,
    `IChannelAddressResolver`, `IChannelOutputRenderer`,
    `IChannelOutboundClient`
  - `IChannel.cs` — `IChannel`, `IRemoteChatChannelOptions`,
    `IGatewaySnapshot`, `GatewayChannelHealth`
  - `ProactiveSendFormatting.cs` — canonical LLM-visible result strings for
    proactive sends
- **`src/Netclaw.Daemon/Configuration/RemoteChatChannelBuilder.cs`** — the
  `AddRemoteChatChannel<TChannel, TOptions>` registration builder.

```text
                 GENERIC (you inherit / call)                PER-CHANNEL (you write)
┌──────────────────────────────────────────────┐   ┌─────────────────────────────────────┐
│ Netclaw.Channels                             │   │ Netclaw.Channels.Xxx                │
│                                              │   │                                     │
│  ChannelLifecycleActor<TSnapshot,TConnect> ◄─┼───┤  XxxNetGatewayLifecycleActor        │
│  ChannelGatewayActor<TChannelId>           ◄─┼───┤  XxxGatewayActor                    │
│  ChannelConversationActor<TMessage>        ◄─┼───┤  XxxConversationActor               │
│  IChannel / GatewayChannelHealth           ◄─┼───┤  XxxChannel                         │
│  IChannelOutboundClient                    ◄─┼───┤  XxxProactiveOutboundClient         │
│  IChannelAddressResolver                   ◄─┼───┤  XxxAddressResolver                 │
│  SessionIdFormat ({channelId}/{threadKey}) │   │  XxxNetGatewayTransport (SDK adapter)│
│                                              │   │  XxxNetReplyClient                  │
│ Netclaw.Daemon                               │   │  XxxChannelOptions                  │
│  RemoteChatChannelBuilder ◄──────────────────┼───┤  one builder chain in               │
│   - descriptor (even when disabled)          │   │  ChannelIntegrationRegistration-    │
│   - keyed IChannel + IHostedService          │   │  Extensions.cs                      │
│   - shared tools (send/lookup), once         │   │                                     │
└──────────────────────────────────────────────┘   └─────────────────────────────────────┘

       Runtime actor hierarchy (all structure provided by the bases):

  XxxChannel (IHostedService)
     │ StartAsync: connect transport, spawn gateway, register actor-registry key
     ▼
  XxxGatewayActor : ChannelGatewayActor<XxxChannelId>
     │ dedup, route by channel id
     ▼
  XxxConversationActor : ChannelConversationActor<XxxGatewayMessage>
     │ ACL → bot filter → ingress gate → routing policy → normalize → forward
     ▼
  XxxSessionBindingActor  (per-thread; runs the LLM session pipeline)
```

## What you write (Mattermost actuals)

The SPEC-015 "what stays per-channel" core is five files of genuinely new
logic plus a builder chain; the actors are subclasses whose structural code
lives in the bases. Real line counts from Mattermost:

| File | LOC | Role |
|---|---|---|
| `MattermostChannelOptions.cs` | 44 | Config binding (`IRemoteChatChannelOptions`) |
| `MattermostTransportContracts.cs` | 253 | Normalized message/snapshot records + client interfaces |
| `Transport/MattermostNetGatewayClient.cs` | 402 | SDK adapter: transport + gateway client hosting the lifecycle actor |
| `Transport/MattermostNetReplyClient.cs` | 110 | Replies into existing threads |
| `Transport/MattermostNetOutboundClient.cs` | 37 | Raw platform posts (proactive) |
| `MattermostProactiveOutboundClient.cs` | 115 | `IChannelOutboundClient`: ACL + post + session wiring |
| `MattermostDestinationAddressResolver.cs` | 84 | `IChannelAddressResolver` |
| `MattermostConnectFailureClassifier.cs` | 64 | Fatal-vs-transient connect failure classification |
| `Transport/MattermostNetGatewayLifecycleActor.cs` | 276 | `ChannelLifecycleActor` subclass (hooks only) |
| `MattermostGatewayActor.cs` | 112 | `ChannelGatewayActor` subclass (3 projections) |
| `MattermostConversationActor.cs` | 259 | `ChannelConversationActor` subclass (13 projections) |
| `MattermostChannel.cs` | 311 | `IChannel` hosted service |
| `MattermostAclPolicy.cs` / `MattermostRoutingPolicy.cs` | 78 / 86 | Pure-function ACL + routing decisions |
| `MattermostReminderTargetResolver.cs` | 104 | Reminder target resolution |
| `Transport/MattermostThreadHistoryFetcher.cs` | 568 | Thread rehydration (optional but expected) |
| `MattermostSessionBindingActor.cs` | 1,576 | Per-thread session actor — the largest remaining per-channel piece |
| Builder chain in `ChannelIntegrationRegistrationExtensions.cs` | ~80 | All DI wiring |

Honest framing: the session binding actor and the SDK transport adapter are
where the real work is. Everything else is either a thin subclass or a small
pure-function policy, and the contract test suites
(`src/Netclaw.Actors.Tests/Channels/Contracts/`) pin the required behavior
of all of it.

## Step-by-step

Throughout, `Xxx` is your channel; concrete examples are Mattermost.

### 1. Add the `ChannelType` enum value and actor-registry key

**File:** `src/Netclaw.Actors/Channels/ChannelType.cs`

Add your channel to the `ChannelType` enum and update `ToWireValue()`,
`TryFromWireValue()`, and `SupportsInteractiveApproval()`. The enum name
("Mattermost") becomes the config section name and display name; the wire
value ("mattermost") becomes the descriptor key and the keyed-service key —
the builder derives all three from the enum
(`RemoteChatChannelBuilder.cs`, `AddRemoteChatChannel`).

**File:** `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`

```csharp
public sealed class XxxGatewayActorKey;
```

Your `IChannel.StartAsync` registers the spawned gateway under this key
(see `MattermostChannel.cs`:
`ActorRegistry.For(_system).Register<MattermostGatewayActorKey>(_gateway)`);
reminder delivery resolves it to route trusted session turns.

### 2. Define the options class

**File:** `src/Netclaw.Channels.Xxx/XxxChannelOptions.cs`

Implement `IRemoteChatChannelOptions` (defined in
`src/Netclaw.Channels/IChannel.cs`) — the builder reads `Enabled` and
`AllowDirectMessages` to build the descriptor without knowing your concrete
type:

```csharp
public sealed class XxxChannelOptions : IRemoteChatChannelOptions
{
    public bool Enabled { get; init; }
    public SensitiveString? BotToken { get; init; }
    public string? DefaultChannelId { get; init; }
    public bool AllowDirectMessages { get; init; }
    public bool MentionOnly { get; init; } = true;
    public bool MentionRequiredInDm { get; init; }
    public string[] AllowedChannelIds { get; init; } = [];
    public string[] AllowedUserIds { get; init; } = [];
    // ... platform-specific knobs (see MattermostChannelOptions.cs)
}
```

Use `SensitiveString` for tokens. The builder binds the section named after
the enum value (`configuration.GetSection("Xxx")`).

> **Config schema sync rule (CLAUDE.md):** add a matching top-level `"Xxx"`
> section to `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`
> in the same PR. The schema is `"additionalProperties": false` throughout —
> unlisted properties are rejected by `ConfigSchemaDoctorCheck` at runtime.
> New required properties need a `"default"`; enums must be `"type": "string"`
> with named values.

### 3. Implement the transport adapter and connect-failure classifier

This is the real SDK work. Define the channel's normalized contracts first
(Mattermost: `MattermostTransportContracts.cs`):

- an inbound message record (`MattermostGatewayMessage`) — what the gateway
  and conversation actors route
- a snapshot record implementing `IGatewaySnapshot`
  (`MattermostGatewaySnapshot`: `IsConnected`, `IsReady`, `HealthDetail`,
  plus your bot identity fields)
- a gateway client interface (`IMattermostGatewayClient`) exposing
  `GetSnapshotAsync` / `ConnectAsync` / `DisconnectAsync`, the
  `MessageReceived` / `CleanReconnectRequired` / `ConnectionRestored`
  events, and identity properties
- a reply client interface (`IMattermostReplyClient`)

Then the adapter itself (Mattermost: `Transport/MattermostNetGatewayClient.cs`):
a transport interface over the raw SDK (`IMattermostGatewayTransport`, with
`StartAsync`/`StopAsync` and `Connected`/`Disconnected`/`MessageReceived`
events) and a gateway client that hosts the lifecycle actor and translates
its ask protocol into the `IXxxGatewayClient` surface.

**Connect-failure classifier** (Mattermost:
`MattermostConnectFailureClassifier.cs`): a static `Classify(Exception)`
returning `ChannelConnectException` with `ChannelConnectFailureKind.Fatal`
(bad token, bad URL — auto-reconnect is disabled, see
`ChannelLifecycleActor.HandleStartFailed`) or `Transient` (network — retried
with exponential backoff). Misclassifying a fatal failure as transient makes
the daemon hammer the platform forever; write tests for it (step 10).

### 4. Lifecycle actor subclass (channels with a managed WebSocket)

**File:** `src/Netclaw.Channels.Xxx/Transport/XxxNetGatewayLifecycleActor.cs`

Only needed when the channel manages its own persistent connection. Discord
and Mattermost have one; Slack does not (SlackNet's socket-mode client
manages its own connection).

Subclass `ChannelLifecycleActor<TSnapshot, TConnectCommand>`
(`src/Netclaw.Channels/ChannelLifecycleActor.cs`). The base owns the entire
state machine — Disconnected → Connecting → Ready → Disconnecting, the
`Connect`/`Disconnect`/`GetSnapshot` ask protocol, exponential-backoff
auto-reconnect with a 60-second stability window, and `PostStop` timer
cleanup. You supply:

- `StartTransportAsync` / `StopTransportAsync` — the transport calls
- `OnTransportStartSucceeded` — validate the start result; channels whose
  start implies readiness call `CompleteConnectToReady()`
- `ClassifyStartFailure` — delegate to your classifier
- `CreateSnapshot` / `ResetIdentityState` — snapshot factory and identity
  reset
- `SubscribeTransportEvents` / `UnsubscribeTransportEvents` — forward
  transport events to `SelfRef` (never `Self`; callbacks run off-dispatcher)
- `PublishCleanReconnectRequiredAsync` / `PublishConnectionRestoredAsync` —
  event-sink publishes
- `IsTransportConnected` / `HasBotIdentity` — readiness inputs
- the six `Register*Handlers` hooks for channel transport events:
  `RegisterCommonChannelHandlers`, `RegisterNotReadyIngressHandlers`, and one
  per state (`Disconnected`/`Connecting`/`Ready`/`Disconnecting`)

**The `ReadySignalTimeout` decision.** The base's
`protected virtual TimeSpan? ReadySignalTimeout => null;` encodes whether
your transport start implies readiness:

- **Null (default, Mattermost):** `StartTransportAsync` returning success
  means the channel is ready — `OnTransportStartSucceeded` calls
  `CompleteConnectToReady()` and no timer is armed.
- **Override (Discord, 30 s):** the transport start only begins a handshake
  and a separate ready event completes it
  (`DiscordNetGatewayLifecycleActor.ReadySignalTimeout`). The base arms a
  timer per connect attempt; if the ready signal never arrives it takes the
  canonical fail path. For transports that reconnect on their own after a
  drop, `WaitForReadySignal()` re-enters Connecting with a fresh attempt
  stamp without tearing the client down.

**The canonical fail path is `RequestCleanReconnect(reason)`.** It fails any
pending operator ask, publishes `CleanReconnectRequired` once per cycle,
stops the transport, and lands in Disconnected with the auto-retry scheduled
— one path whether or not an operator ask is pending. Do not invent your own
teardown branch: the comment in `HandleReadySignalTimedOut` documents the
0.24.0-beta.2 zombie that branching here produced (an actor parked with no
retry scheduled, silently dropping all traffic).

**Constructor caveat:** the base constructor calls `Become`, which invokes
your `Register*` hooks *before your subclass constructor body runs*. Hooks
must only register handlers — never read subclass fields at registration
time (documented on the base constructor).

### 5. Gateway and conversation actor subclasses

**File:** `src/Netclaw.Channels.Xxx/XxxGatewayActor.cs`

Subclass `ChannelGatewayActor<TChannelId>`
(`src/Netclaw.Channels/ChannelGatewayActor.cs`) with your channel-id value
type as `TChannelId`. The base provides bounded duplicate-event tracking
(`TryMarkEventProcessed`, 4,096 ids, oldest-first eviction), conversation
child get-or-create (`GetOrCreateConversation`), and the
`DeliverTrustedSessionTurn` receive for reminder delivery. You implement
three projections — `ChannelIdValue`, `CreateConversationProps`,
`TryParseSessionChannelId` — and register your channel-specific inbound
receives in your own constructor. Override `OnMissingEventId()` only if your
platform legitimately emits id-less events (Slack does; the default rejects
them because they cannot be deduplicated).

**Session-id grammar:** `SessionIdFormat` (top of `ChannelGatewayActor.cs`)
is the single owner of the `{channelId}/{threadKey}` format. Build ids with
`SessionIdFormat.Build` and parse with `SessionIdFormat.TrySplit` — session
ids are persisted (they key session state and reminders), so the format is a
breaking migration, never a refactor. Write a typed
`TryParseXxxSessionId` wrapper like `MattermostGatewayActor.TryParseMattermostSessionId`.

**File:** `src/Netclaw.Channels.Xxx/XxxConversationActor.cs`

Subclass `ChannelConversationActor<TMessage>`
(`src/Netclaw.Channels/ChannelConversationActor.cs`). The base owns the
inbound security pipeline in a **fixed order** — do not reorder or bypass:

1. ACL gate (`EvaluateAcl`)
2. Bot self-loop filter (`IsBotMessage`)
3. Ingress gate (restart drain via `SessionIngressGate`)
4. Routing policy (`EvaluateRouting` → `ChannelRoutingVerdict`:
   `StartOrContinue` / `ContinueOnly` / `Ignore`)
5. Text normalization, 4,000-char truncation, empty-text filter
6. Session binding get-or-create and forward

It also owns 2-hour idle passivation, stop-on-failure supervision of session
bindings, and `Terminated` bookkeeping. You implement 13 abstract members:
two log-context keys (`ThreadLogContextKey`, `EventLogContextKey`) and
eleven projections/handlers (`EvaluateAcl`, `IsBotMessage`, `EventIdOf`,
`ThreadKeyOf`, `TextOf`, `HasAttachments`, `NormalizeInboundText`,
`EvaluateRouting`, `CreateSessionBindingProps`, `CreateThreadInbound`,
`PostIngressClosedReplyAsync`). Keep the ACL and routing decisions
themselves in pure-function policy classes (`MattermostAclPolicy`,
`MattermostRoutingPolicy`) so the contract tests can exercise them without
an actor system.

> Slack's conversation actor intentionally does **not** use this base — its
> pipeline order differs observably (see the remark on
> `ChannelConversationActor` and SPEC-015 §1.3). New channels should use the
> base; Mattermost is the canonical example.

**Session binding actor** (`CreateSessionBindingProps` target): there is no
generic base — this per-thread actor runs the LLM session pipeline (prompt
injection gate, approvals, output posting, failure notification) and is the
largest per-channel file. Clone `MattermostSessionBindingActor.cs` and
adapt; `SessionBindingContractTests` (16 assertions) pins its required
behavior.

### 6. Reply client and proactive outbound client

**Reply client** (`Transport/XxxNetReplyClient.cs` behind
`IXxxReplyClient`): posts replies into an existing thread; consumed by the
session binding actor and any output renderer.

**Raw outbound client** (`Transport/XxxNetOutboundClient.cs` behind
`IXxxOutboundClient`): the thin SDK wrapper for proactive posts (new thread
in a channel, open a DM). Mattermost's is 37 lines.

**Proactive outbound client** (`XxxProactiveOutboundClient.cs`): implements
`IChannelOutboundClient` (`src/Netclaw.Channels/ChannelDeliveryContracts.cs`)
— the generic `send_channel_message` tool dispatches to it by channel key.
It owns, in order: the channel's outbound ACL checks (allowed
channels/users, the `AllowDirectMessages` gate), the platform post via the
raw outbound client, and wiring the new thread into the session pipeline by
asking the gateway actor (waiting `ProactiveSendFormatting.ProactiveThreadAckTimeout`,
30 s).

Two invariants:

- **ACL sits above the SDK seam.** The proactive client wraps the raw
  outbound interface rather than living inside it, because the ACL checks
  must be above the fake seam used in tests (SPEC-015 §1.5). Never push an
  allowlist check down into the SDK adapter.
- **Result strings are the LLM-visible tool contract.** Build every outcome
  from `ProactiveSendFormatting`
  (`GatewayNotConnected`, `DirectMessagesDisabled`, `UserNotAllowed`,
  `ChannelNotAllowed`, `UnsupportedAddressKind`, `OpenDmChannelFailed`,
  `PostFailed`, `DescribeTarget`, `Sent`, `SentButPipelineFailed`) — they
  must stay byte-stable and identical across channels. Return `Error: ...`
  strings for expected failures; never throw (documented on
  `IChannelOutboundClient`).

### 7. Address resolver

**File:** `src/Netclaw.Channels.Xxx/XxxAddressResolver.cs`

Implement `IChannelAddressResolver`: a `Key`
(`ChannelDescriptorKey.FromChannelType(ChannelType.Xxx)`), the
`AddressKinds` you handle, and `ResolveAsync` mapping human-friendly targets
(`#channel`, `@user`, raw platform ids) to
`ChannelAddressResolutionResult.Resolved` / `NotFound` / `Ambiguous` /
`Unsupported`, with your ACL applied. A channel may register multiple
resolvers as long as their `(Key, AddressKind)` pairs don't collide — the
registry indexes by that pair (Mattermost registers
`MattermostDestinationAddressResolver` for destinations and
`LookupMattermostUserTool` for users).

**`ListDestinationsAsync` is opt-in and bounded-sets-only.** The default
implementation on the interface returns a loud `Unsupported`. Override it
only for *destinations* — the deliverable destination set is bounded (bot
memberships, guild channels, or a configured allowlist) while user
directories are unbounded and only make sense as server-side searches
(documented on `IChannelAddressResolver`). Return
`ChannelAddressResolutionResult.Listed(...)`; an empty list is a valid
listing, and the same ACL gating as `ResolveAsync` applies.

### 8. Renderer, reminder resolver, thread history

**Output renderer (optional):** the session binding actor posts text replies
directly through the reply client; an `IChannelOutputRenderer` is only
needed for *additional* output effects beyond the
`TextMessage`/`FileAttachment` baseline — e.g. Discord's typing indicator
(`DiscordProcessingOutputRenderer` handling
`ChannelOutputEffectKind.ProcessingIndicator`). Declare the extra effects
via the `additionalOutputEffects` parameter of `AddRemoteChatChannel` and
register the renderer with `.WithRenderer<T>()`. Mattermost registers none.

**Reminder target resolver:** implement `IReminderTargetResolver`
(`src/Netclaw.Actors/Reminders/IReminderTargetResolver.cs`) so reminders can
address `@user`/`#channel` targets on your platform; register with
`.WithReminderResolver<T>()` (Mattermost: `MattermostReminderTargetResolver`).

**Thread history fetcher:** implement `IThreadHistoryFetcher`
(`src/Netclaw.Actors/Channels/IThreadHistoryFetcher.cs`) to rehydrate prior
thread messages when a session is re-created. **Registration is keyed by
channel key, and the keying is load-bearing:** with two or more channels
enabled, an unkeyed registration would resolve to the *last* channel's
fetcher for every channel, silently cross-wiring thread rehydration
(documented on `RemoteChatChannelBuilder.WithThreadHistory` and the channel
factory in `AddRemoteChatChannel`, which resolves the keyed fetcher and
passes it to your channel's constructor explicitly). Always register through
`.WithThreadHistory(...)` — never `services.AddSingleton<IThreadHistoryFetcher>`.

### 9. The `IChannel` service and the builder chain

**File:** `src/Netclaw.Channels.Xxx/XxxChannel.cs`

The hosted service that ties it together (Mattermost: `MattermostChannel.cs`):

- `StartAsync` — connect the gateway client, spawn the gateway actor,
  register it in `ActorRegistry` under your `XxxGatewayActorKey`. A
  misconfigured channel **degrades, never throws** — a missing token is a
  contained channel failure with an operational alert, not a daemon crash.
- `GetHealthAsync` — for snapshot transports, one call:
  `GatewayChannelHealth.Evaluate(snapshot, connectFailureDetail, notReadyFallback, disconnectedFallback)`
  (`src/Netclaw.Channels/IChannel.cs`).
- Expose the spawned gateway (`internal IActorRef? Gateway`) and any
  runtime-resolved values (e.g. a default channel id resolved from a
  configured name) — the lazy accessors below read them.

**Registration:** one fluent chain in
`src/Netclaw.Daemon/Configuration/ChannelIntegrationRegistrationExtensions.cs`,
added to `AddChannelIntegrations` (which `Program.cs` already calls).
Mattermost's chain, abbreviated:

```csharp
internal static void AddXxxChannel(IServiceCollection services, IConfiguration configuration)
{
    services.AddRemoteChatChannel<XxxChannel, XxxChannelOptions>(ChannelType.Xxx, configuration)
        .WithServices((channelServices, options) =>
        {
            // Escape hatch for SDK-specific registrations (the raw API client).
            // Use an "unconfigured" placeholder for missing tokens — throwing
            // here aborts host construction; XxxChannel.StartAsync degrades
            // the channel loudly instead (see issue #1033).
        })
        .WithFilesHttpClient()                                            // "{key}-files" HttpClient + Netclaw headers
        .WithTransport<IXxxGatewayClient, XxxNetGatewayClient>()
        .WithReplyClient<IXxxReplyClient, XxxNetReplyClient>()
        .WithOutboundClient<IXxxOutboundClient, XxxNetOutboundClient>()
        .WithThreadHistory((sp, options) => new XxxThreadHistoryFetcher(/* ... */))  // keyed!
        .WithReminderResolver<XxxReminderTargetResolver>()
        .WithResolver((sp, options) => new XxxAddressResolver(/* ... */))
        .WithProactiveSendClient((sp, options) => new XxxProactiveOutboundClient(
            sp.GetRequiredService<IXxxOutboundClient>(),
            options,
            () => sp.GetRequiredService<XxxChannel>().DefaultChannelId,   // LAZY
            () => sp.GetRequiredService<XxxChannel>().Gateway))           // LAZY
        .WithLookupTool((sp, options) => new LookupXxxUserTool(/* ... */));
}
```

Other builder methods when you need them: `WithLookupClient<TService, TImpl>`
(the lookup SDK wrapper behind your user-lookup tool) and
`WithRenderer<TRenderer>` (step 8). Pass extra output effects as the third
argument to `AddRemoteChatChannel` (Discord passes
`ChannelOutputEffectKind.ProcessingIndicator`).

**The lazy accessor rule.** `ChannelRegistry` constructs every registered
`IChannelAddressResolver` and `IChannelOutboundClient` **eagerly** at
startup — before any channel exists. Factories passed to `WithResolver` /
`WithProactiveSendClient` must therefore never resolve the channel (or
anything reached through it, like the gateway actor ref) eagerly: capture
`Func<...>` accessors that call `sp.GetRequiredService<XxxChannel>()` at
*invocation* time. An eager resolution recurses through the registry's own
construction (channel → registry → outbound client → channel) — the
comments on all three existing chains in
`ChannelIntegrationRegistrationExtensions.cs` spell this out.

**What the builder does for you — do not do these manually:**

- binds options from the section named after the enum and registers them as
  a singleton
- registers the `ChannelDescriptor` via `ChannelDescriptor.CreateRemoteChat`
  **always, even when the channel is disabled** (the registry lists disabled
  channels), with a runtime snapshot provider
- registers the keyed `IChannel`, its unkeyed forward, the concrete
  `XxxChannel` forward, and the `IHostedService` forward
- registers the shared channel tools — `SendChannelMessageTool`,
  `LookupChannelUserTool`, `LookupChannelDestinationTool` — exactly once
  across all channels (`AddSharedChannelTools`); there are **no per-channel
  send tools** anymore (SPEC-015 §1.5)
- turns every `With*` call into a no-op when the channel is disabled

### 10. Tests

#### Contract suites (inherit, don't rewrite)

Each abstract base in `src/Netclaw.Actors.Tests/Channels/Contracts/` asserts
shared security-critical behavior once; your channel proves compliance by
providing a small fixture. `Contracts/README.md` documents the full
inventory and the fixture how-to for each — follow it. A new channel
inherits:

| Base | Assertions | Notes |
|---|---|---|
| `AclPolicyContractTests` | 16 | Pure functions, no TestKit |
| `GatewayRoutingContractTests` | 3 | Routing, dedup, gateway-level ACL |
| `RoutingPolicyContractTests` | 11 | Mention gating, thread continuation, DM matrix |
| `SessionBindingContractTests` | 16 | Injection gate, approvals, output, failure paths |
| `ChannelHealthContractTests` | 3 | Use `SnapshotChannelHealthContractTests` (+2) for snapshot transports |
| `GatewayLifecycleContractTests` | 7 | Only if you have a lifecycle actor; runs on `TestScheduler` virtual time |
| `ProactiveOutboundClientContractTests` | 7 | LLM-visible result strings pinned literally |

```bash
dotnet test src/Netclaw.Actors.Tests/ --filter "FullyQualifiedName~Contracts"
```

#### Channel-specific tests

In `src/Netclaw.Actors.Tests/Channels/` (outside `Contracts/`):

- [ ] Connect-failure classifier: fatal vs. transient mapping for your SDK's
      exception shapes (`MattermostConnectFailureClassifierTests`)
- [ ] Thread-history fetcher (`MattermostThreadHistoryFetcherTests`)
- [ ] Message chunking, proactive thread creation, approval prompt building,
      and any platform-unique routing-policy reasons (the contract base
      tells you to throw on channel-specific ignore reasons — they belong
      here)

#### Registration tests

Add cases to
`src/Netclaw.Daemon.Tests/Configuration/ChannelRegistryRegistrationTests.cs`:

- [ ] Descriptor appears in `ListChannels()` with correct key, kind,
      capabilities, and `SupportedOutputEffects`
- [ ] Disabled channel still lists a disabled descriptor
- [ ] Resolver, outbound client, and (if any) renderer are routable by key

## Definition-of-done checklist

- [ ] Config schema updated in the same PR
      (`netclaw-config.v1.schema.json`; CLAUDE.md § Configuration Schema
      Sync Rule), and `netclaw doctor` validates the new section
- [ ] All contract suites inherited and green; channel-specific tests added
- [ ] `dotnet slopwatch analyze` — no new violations
- [ ] `./scripts/Add-FileHeaders.ps1 -Verify` — headers on all new `.cs`
      files
- [ ] System skill sync (CLAUDE.md table): adding a channel through the
      builder adds **no new tools** (the shared send/lookup tools already
      exist), so no skill update is needed *unless* you change a tool
      schema or add a channel-specific tool — then update
      `netclaw-operations` and run the eval suite (`./evals/run-evals.sh`)
- [ ] Operational impact documented (this runbook, `CONTRIBUTING.md`
      project-structure list, CLI help if applicable)

## Files changed summary

| Layer | Files |
|-------|-------|
| Enum | `src/Netclaw.Actors/Channels/ChannelType.cs` |
| Actor key | `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs` |
| Channel project | `src/Netclaw.Channels.Xxx/` (new; reference `Netclaw.Channels` + `Netclaw.Actors`) |
| Options | `XxxChannelOptions.cs` (implements `IRemoteChatChannelOptions`) |
| Transport | `XxxTransportContracts.cs`, `Transport/XxxNetGatewayClient.cs` |
| Failure classifier | `XxxConnectFailureClassifier.cs` |
| Lifecycle actor | `Transport/XxxNetGatewayLifecycleActor.cs` (managed WebSocket only) |
| Actors | `XxxGatewayActor.cs`, `XxxConversationActor.cs`, `XxxSessionBindingActor.cs` |
| Policies | `XxxAclPolicy.cs`, `XxxRoutingPolicy.cs` |
| Reply/outbound | `Transport/XxxNetReplyClient.cs`, `Transport/XxxNetOutboundClient.cs`, `XxxProactiveOutboundClient.cs` |
| Channel service | `XxxChannel.cs` |
| Address resolver | `XxxAddressResolver.cs` |
| Reminder resolver | `XxxReminderTargetResolver.cs` |
| Thread history | `Transport/XxxThreadHistoryFetcher.cs` (registered keyed) |
| DI wiring | one chain in `src/Netclaw.Daemon/Configuration/ChannelIntegrationRegistrationExtensions.cs` |
| Config schema | `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` |
| Contract fixtures | `src/Netclaw.Actors.Tests/Channels/Contracts/Xxx*ContractTests.cs` |
| Channel tests | `src/Netclaw.Actors.Tests/Channels/Xxx*.cs` |
| Registration tests | `src/Netclaw.Daemon.Tests/Configuration/ChannelRegistryRegistrationTests.cs` |
