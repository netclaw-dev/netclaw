# Channel Contract Tests

This directory contains abstract test base classes that define the behavioral specification for Netclaw channel adapters. Each base class asserts security-critical behavior once; channel implementations prove they satisfy the contracts by providing factory methods.

## Why contracts?

Slack and Discord implement identical security logic independently: ACL policies, prompt injection gating, approval flows, failure notifications, and session lifecycle. A security fix in one channel must be verified in the other. Contract tests enforce this by running the same assertions against every channel adapter.

## Test inventory

| Base class | Tests | Requires TestKit | What it covers |
|---|---|---|---|
| `PromptClassificationTests` | 10 | No | `PromptClassifier.ClassifyAsync` (shared code, no abstract base) |
| `AclPolicyContractTests` | 16 | No | ACL allow/deny, audience resolution, principal classification, provenance |
| `GatewayRoutingContractTests` | 3 | Yes | Message routing, duplicate event filtering, ACL enforcement at gateway |
| `RoutingPolicyContractTests` | 11 | No | Inbound routing policy: mention gating, thread continuation/rehydration, DM matrix, empty content |
| `SessionBindingContractTests` | 16 | Yes | Prompt injection gate, approval flows, output rendering, failure notification, pipeline lifecycle |
| `ChannelHealthContractTests` | 3 | Yes | `IChannel.GetHealthAsync`: healthy when connected+ready, disconnected with a reason, degraded when disabled |
| `SnapshotChannelHealthContractTests` | +2 | Yes | Snapshot transports only (Discord, Mattermost): degraded when connected-but-not-ready, health detail propagated from the transport snapshot. Slack's socket-mode transport is binary and implements only the base health contract |
| `ChannelShutdownContractTests` | 1 | No | `IChannel.StopAsync` must not propagate a failing transport disconnect — shutdown teardown races (dead actor system on SIGTERM) are expected, and a throw becomes a false `daemon-main` crash. Fixtures wire a timing-out fake transport and assert `StopAsync` completes |
| `GatewayLifecycleContractTests` | 7 | Yes | Gateway lifecycle state machine (Discord and Mattermost only — Slack has no lifecycle actor): not-ready ingress dropped, runtime disconnect → clean reconnect, spurious ready signal → clean reconnect, no duplicate transport handlers across reconnects, not-ready reported while transport still connected, auto-reconnect after disconnect, retry timer cancelled on actor stop. Runs on `Akka.TestKit.TestScheduler` (virtual time); retry/ready-timeout timers are driven via `AdvanceScheduler` |

Total: **59 contract assertions per channel** (16 ACL + 3 gateway + 11 routing policy + 16 session binding + 3 health + 10 shared prompt classification), plus 2 extra health assertions for snapshot-transport channels and 7 lifecycle assertions for channels with a gateway lifecycle actor.

## Adding a new channel

You need four implementation classes, each providing factory methods that map channel-neutral inputs to your channel's types.

### 1. ACL contract: `{Channel}AclContractTests`

Subclass `AclPolicyContractTests`. No TestKit needed — ACL policies are static methods.

```csharp
public sealed class ExampleAclContractTests : AclPolicyContractTests
{
    protected override string ExpectedSourceKind => "example";

    protected override IAclDecision EvaluateDm(string userId, ChannelOptionsBuilder options)
        => EvaluateMessage("dm-channel", userId, isDm: true, options);

    protected override IAclDecision EvaluateChannel(
        string channelId, string userId, ChannelOptionsBuilder options)
        => EvaluateMessage(channelId, userId, isDm: false, options);

    protected override IAclDecision EvaluateMessage(
        string channelId, string userId, bool isDm, ChannelOptionsBuilder options)
    {
        // 1. Map ChannelOptionsBuilder → your channel's options type
        // 2. Construct your channel's inbound message type
        // 3. Call your AclPolicy.EvaluateInbound(message, options, defaultChannelId)
        // 4. Return the IAclDecision
    }
}
```

Your `*AclDecision` type must implement `IAclDecision` (defined in `Netclaw.Channels`).

### 2. Gateway contract: `{Channel}GatewayContractTests`

Subclass `GatewayRoutingContractTests`. Requires Akka.Hosting.TestKit.

```csharp
public sealed class ExampleGatewayContractTests(ITestOutputHelper output)
    : GatewayRoutingContractTests(output)
{
    protected override IActorRef CreateGateway(ChannelOptionsBuilder options)
    {
        // Construct your gateway actor with a ForwardActor as the session sink.
        // Use TestActor as the forward target so the base class can assert on routed messages.
    }

    protected override object CreateAllowedMessage(
        string channelId, string threadId, string userId, string text, string eventId)
    {
        // Construct a message that will pass ACL (channel in allowlist, valid user)
    }

    protected override object CreateDeniedMessage(
        string channelId, string userId, string eventId)
    {
        // Construct a message that will fail ACL (channel not in allowlist)
    }
}
```

### 3. Routing policy contract: `{Channel}RoutingPolicyContractTests`

Subclass `RoutingPolicyContractTests`. No TestKit needed — routing policies are static methods.

```csharp
public sealed class ExampleRoutingPolicyContractTests : RoutingPolicyContractTests
{
    protected override RoutingVerdict Evaluate(
        bool mentionOnly, bool allowDm, bool mentionRequiredInDm,
        bool isDm, bool containsMention, bool threadExists, bool isThreadReply, string text)
    {
        // 1. Construct a plain text-only inbound message for your channel
        //    (no attachments, no platform-specific subtypes), mapping
        //    isThreadReply onto however your platform marks thread replies.
        // 2. Call your ExampleRoutingPolicy.Evaluate(...)
        // 3. Map your decision kind/ignore reason onto RoutingVerdict.
        //    Throw on channel-specific ignore reasons — they belong in
        //    standalone tests, not the contract.
    }
}
```

### 4. Session binding contract: `{Channel}SessionBindingContractTests`

Subclass `SessionBindingContractTests`. This is the largest contract — it tests the full actor lifecycle.

```csharp
public sealed class ExampleSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private RecordingExampleReplyClient _replyClient = new();

    // Required: create the binding actor with test dependencies
    protected override IActorRef CreateBindingActor(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
    {
        // Reset reply client (preserve ThrowOnPost if set)
        // Construct your channel's dependencies record
        // Return Sys.ActorOf(YourBindingActor.CreateProps(...))
    }

    // Required: construct a channel-specific inbound message
    protected override object CreateInboundMessage(string text, string senderId) { }

    // Required: construct a channel-specific approval response
    protected override object CreateApprovalResponse(
        string callId, string selectedKey, string senderId) { }

    // Required: read posted texts from your recording reply client
    protected override IReadOnlyList<string> GetPostedTexts()
        => _replyClient.Posts.Select(p => p.Text).ToList();

    protected override void ClearPostedTexts() => _replyClient.Clear();

    protected override void SetReplyClientThrows(Exception ex)
        => _replyClient.ThrowOnPost = ex;

    protected override void ClearReplyClientThrows()
        => _replyClient.ThrowOnPost = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Example;
}
```

### 5. Health contract: `{Channel}ChannelHealthContractTests`

Subclass `ChannelHealthContractTests` (or `SnapshotChannelHealthContractTests` if your transport exposes a connected/ready snapshot with a health detail, like Discord and Mattermost). Requires Akka.Hosting.TestKit.

```csharp
public sealed class ExampleChannelHealthContractTests(ITestOutputHelper output)
    : SnapshotChannelHealthContractTests(output)
{
    protected override IChannel CreateChannel(bool enabled)
    {
        // Construct your channel wired to a controllable fake transport client.
    }

    protected override Task SetTransportStateAsync(bool connected, bool ready, string? healthDetail)
    {
        // Drive the fake transport into the given state. If your transport can
        // only connect through the channel's own connect path (like Slack),
        // run channel.StartAsync here and throw NotSupportedException for
        // states your transport cannot represent.
    }
}
```

If your binding actor uses persistence (like Slack's `ReceivePersistentActor`), override `ConfigureAkka` to add in-memory journal and snapshot store:

```csharp
protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
{
    builder.WithInMemoryJournal().WithInMemorySnapshotStore();
}
```

Use `Interlocked.Increment` for unique actor names when persistence requires unique `PersistenceId` values.

### 6. Gateway lifecycle contract: `{Channel}GatewayLifecycleContractTests`

Only for channels with a gateway lifecycle actor (a WebSocket transport the
channel manages itself — Discord and Mattermost today; Slack's socket-mode
client manages its own connection and is excluded). Subclass
`GatewayLifecycleContractTests`. Requires Akka.Hosting.TestKit.

The base runs on `Akka.TestKit.TestScheduler` (virtual time): retry timers and
ready timeouts never fire on their own — tests drive them with
`AdvanceScheduler(offset)`. Your fixture provides:

- `CreateLifecycleActor()` — wire your lifecycle actor to a fresh fake
  transport and recording event sink, stored on the fixture
- `GetSnapshotAsync` / `ConnectAsync` / `DisconnectAsync` — drive the actor's
  ask protocol and normalize your snapshot type to `LifecycleSnapshotView`
- `RaiseRuntimeDisconnectAsync` — raise a transport drop and drive the actor
  to the clean-reconnect decision (advance virtual time past your ready
  timeout if your channel defers it, like Discord's 30s READY wait)
- `RaiseSpuriousReadySignalAsync` / `RaiseIngressEventAsync` — fire transport
  events outside a clean startup cycle
- Subscriber-count assertions plus `DeferTransportStop`/`ReleaseTransportStop`
  on the fake transport (a deferrable `StopAsync` lets the contract observe
  the not-ready-while-still-connected teardown window)

Lifecycle behaviors unique to your platform stay as extra `[Fact]`s on the
fixture — e.g. Discord's spurious-Connected-while-Ready clean reconnect, and
Mattermost's ingress-forwarded-exactly-once-after-reconnect check (Discord
cannot construct a forwardable `SocketUserMessage`).

## Shared test helpers

All helpers live in `../TestHelpers/`:

| Helper | Purpose |
|---|---|
| `RecordingSessionPipeline` | Captures `SendFeedbackAsync` calls; produces scripted `SessionOutput` from a pre-configured list |
| `ConfigurablePromptInjectionDetector` | Returns a fixed `PromptInjectionResult` or throws a configured exception |
| `FailingSessionPipeline` | Throws on `CreateAsync` to test init failure handling |
| `ForwardActor` | Forwards all messages to a `TestProbe` for assertion |
| `ChannelOptionsBuilder` | Channel-neutral options record that each contract maps to channel-specific options |
| `RecordingDiscordReplyClient` | Records Discord reply posts; supports `ThrowOnPost` for failure simulation |
| `RecordingSlackReplyClient` | Records Slack reply posts; supports `ThrowOnPost` for failure simulation |

For your new channel, you'll need a `Recording{Channel}ReplyClient` that implements your channel's reply client interface with the same pattern: thread-safe post recording, `ThrowOnPost`, and `Clear()`.

## Channel-specific tests

Contract tests cover shared behavioral requirements. You should still add channel-specific tests in `../` (outside this directory) for behavior unique to your channel:

- Bot message filtering (Discord-specific)
- Interaction component routing (Discord-specific)
- Thread history backfill (Slack-specific)
- file_share subtype / hidden message / BlockAction handling (Slack-specific)
- Attachment processing (Slack-specific)

The rule of thumb: if the behavior exists in your channel but not others, test it separately. If the behavior should be identical across channels, it belongs in a contract.

## Running contract tests

```bash
# All contract tests
dotnet test src/Netclaw.Actors.Tests/ --filter "FullyQualifiedName~Contracts"

# Single channel's contracts
dotnet test src/Netclaw.Actors.Tests/ --filter "FullyQualifiedName~Discord" --filter "FullyQualifiedName~Contracts"

# Full regression suite (includes contracts + channel-specific)
dotnet test
```
