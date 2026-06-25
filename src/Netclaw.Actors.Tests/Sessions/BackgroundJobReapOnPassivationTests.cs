// -----------------------------------------------------------------------
// <copyright file="BackgroundJobReapOnPassivationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration tests for reap-on-passivation: a session that submitted
/// background jobs must kill them (via the manager handshake) before its final
/// passivation snapshot, surface the reap to the agent exactly once on
/// rehydration, and never wedge on an unresponsive manager.
/// </summary>
public sealed class BackgroundJobReapOnPassivationTests : LlmSessionTestBase
{
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _fakeChatClient = new();

    public BackgroundJobReapOnPassivationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            // Passivation is driven explicitly via ReceiveTimeout.Instance.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(new PermissiveToolExecutor());

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Passivation_reaps_jobs_and_surfaces_reap_exactly_once_on_rehydration()
    {
        var jobManagerProbe = CreateTestProbe("job-manager");
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManagerProbe.Ref, overwrite: true);

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-bg-1", "shell_execute",
                new Dictionary<string, object?>
                {
                    ["command"] = "jekyll serve",
                    ["_background"] = true,
                    ["_rationale"] = "dev server"
                })
        ];

        var sessionId = new SessionId("test-channel/reap-on-passivation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("reap-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Start the dev server",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The pipeline routes the background call to the (probe) manager.
        var startCmd = await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("jekyll serve", startCmd.Command);
        Assert.Equal(0, startCmd.TimeoutSeconds); // no kill timer without an explicit hint
        jobManagerProbe.Reply(new BackgroundJobStarted(
            new BackgroundJobId("reap-job-1"), "/tmp/jobs/reap-job-1/output.log"));

        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);

        // Drop the subscriber so passivation is not deferred, then trigger it.
        child.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        child.Tell(ReceiveTimeout.Instance);

        // The passivating session must request the reap and wait for the ack.
        var kill = await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, kill.SessionId);
        jobManagerProbe.Reply(new SessionJobsReaped(sessionId, 1));

        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);

        // Rehydrate: the next turn's context must surface the reaped job once.
        _fakeChatClient.ToolCallsOnFirstCall = null;
        var llmCallsBefore = _fakeChatClient.ReceivedMessages.Count;
        var subscriberB = CreateTestProbe("reap-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "How is the server doing?",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriberB.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var firstTurnPrompt = FlattenPromptsSince(llmCallsBefore);
        Assert.Contains("status: reaped", firstTurnPrompt);
        Assert.Contains("reap-job-1", firstTurnPrompt);
        Assert.Contains("/tmp/jobs/reap-job-1/output.log", firstTurnPrompt);

        // The reaped entry is pruned after that turn — the next turn's prompt
        // must not regenerate the block.
        var callsBeforeSecondTurn = _fakeChatClient.ReceivedMessages.Count;
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Anything else?",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriberB.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        // Turn 1's injected context block lives on in persisted history, so the
        // string still appears once in turn 2's prompt. Pruning means the block
        // is not REGENERATED — i.e. exactly one (historical) occurrence, not two.
        var secondTurnPrompt = FlattenPromptsSince(callsBeforeSecondTurn);
        Assert.Equal(1, CountOccurrences(secondTurnPrompt, "status: reaped"));
    }

    [Fact]
    public async Task Passivation_proceeds_loudly_when_reap_ack_never_arrives()
    {
        var jobManagerProbe = CreateTestProbe("job-manager-silent");
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManagerProbe.Ref, overwrite: true);

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-bg-2", "shell_execute",
                new Dictionary<string, object?>
                {
                    ["command"] = "npm run dev",
                    ["_background"] = true,
                    ["_rationale"] = "dev server"
                })
        ];

        var sessionId = new SessionId("test-channel/reap-ack-timeout");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("timeout-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Start the dev server",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        jobManagerProbe.Reply(new BackgroundJobStarted(
            new BackgroundJobId("timeout-job-1"), "/tmp/jobs/timeout-job-1/output.log"));

        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);

        child.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        child.Tell(ReceiveTimeout.Instance);

        // The reap request is sent but never acknowledged — passivation must
        // still complete after the ask timeout (fail loud, never wedge).
        await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        await ExpectTerminatedAsync(
            child,
            TimeSpan.FromSeconds(20),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private string FlattenPromptsSince(int callIndex) =>
        string.Join("\n===\n", _fakeChatClient.ReceivedMessages
            .Skip(callIndex)
            .SelectMany(prompt => prompt)
            .Select(m => m.Text ?? string.Empty));

    private MessageSource RequesterSource(string senderId) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId(senderId),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(
            TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = FixedReceivedAt,
    };

    private sealed class PermissiveToolExecutor : IToolExecutor
    {
        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.FromResult("ok");
    }
}
