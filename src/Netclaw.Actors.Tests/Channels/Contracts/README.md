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
| `SessionBindingContractTests` | 16 | Yes | Prompt injection gate, approval flows, output rendering, failure notification, pipeline lifecycle |

Total: **45 contract assertions per channel** (16 ACL + 3 gateway + 16 session binding + 10 shared prompt classification).

## Adding a new channel

You need three implementation classes, each providing factory methods that map channel-neutral inputs to your channel's types.

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

### 3. Session binding contract: `{Channel}SessionBindingContractTests`

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

If your binding actor uses persistence (like Slack's `ReceivePersistentActor`), override `ConfigureAkka` to add in-memory journal and snapshot store:

```csharp
protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
{
    builder.WithInMemoryJournal().WithInMemorySnapshotStore();
}
```

Use `Interlocked.Increment` for unique actor names when persistence requires unique `PersistenceId` values.

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
- Mention-based filtering (Slack-specific)
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
