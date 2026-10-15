// -----------------------------------------------------------------------
// <copyright file="LegacyCoordinationSqliteRecoveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LegacyCoordinationSqliteRecoveryTests : RecordedJournalTestBase
{
    private const string SessionValue = "signalr/child-ledger-owner";
    private readonly FakeChatClient _chatClient = new();
    private readonly CountingToolExecutor _toolExecutor = new();

    public LegacyCoordinationSqliteRecoveryTests(ITestOutputHelper output)
        : base(output, "CoordinationLegacy", "legacy-writer-records.json", "9f79c0c4b637f005ffba5572d0bf50aa550464f2") { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities { ModelId = "legacy-recovery-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0 } });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(_toolExecutor);
        services.AddSingleton(new ToolRegistry());
    }

    [Fact]
    public async Task Sqlite_recovery_replays_a_deployed_child_pair_and_settles_it_without_reexecution()
    {
        var sessionId = new SessionId(SessionValue);
        var persistenceId = $"session-{sessionId.Value}";
        await SeedJournalAsync(persistenceId, "legacy-fixture-seeder",
            "actor-origin-input-one", "actor-origin-input-two", "actor-parent-task-adoption",
            "actor-start-batch", "child-run-accepted", "child-started", "child-checkpoint",
            "child-terminal", "child-result-prepared", "child-delivery-admitted",
            "valid-child-delivery-adoption", "child-downstream-tool-batch",
            "child-downstream-tool-result", "child-downstream-final-turn");

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("legacy-sqlite-session");
        var joined = await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);

        Assert.Equal(1, joined.TurnCount);
        Assert.Equal(0, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);

        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue after the recovered child result."
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);
        var messages = Assert.Single(_chatClient.ReceivedMessages);
        var originalCalls = messages.SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
            .Where(static call => call.CallId == "legacy-delivery-call")
            .ToArray();
        var originalResults = messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Where(static result => result.CallId == "legacy-delivery-call")
            .ToArray();
        Assert.Single(originalCalls);
        Assert.Equal("spawn_agent", originalCalls[0].Name);
        Assert.Single(originalResults);
        Assert.Equal(originalCalls[0].CallId, originalResults[0].CallId);
        Assert.Contains("accepted-run", originalResults[0].Result?.ToString(), StringComparison.Ordinal);
        Assert.Contains("The child accepted-run confirmed its result.", originalResults[0].Result?.ToString(),
            StringComparison.Ordinal);

        var downstreamCall = Assert.Single(messages.SelectMany(static message => message.Contents.OfType<FunctionCallContent>()),
            static call => call.CallId == "downstream-call");
        Assert.Equal("file_read", downstreamCall.Name);
        var downstreamResult = Assert.Single(messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
            static result => result.CallId == "downstream-call");
        Assert.Contains("Neutral downstream result.", downstreamResult.Result?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(messages, static message => message.Role == Microsoft.Extensions.AI.ChatRole.User
            && message.Text?.Contains("The child accepted-run confirmed", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Sqlite_recovery_keeps_an_ambiguous_snapshot_inert_and_out_of_the_input_queue()
    {
        var sessionId = new SessionId(SessionValue);
        var persistenceId = $"session-{sessionId.Value}";
        await SeedSnapshotAsync(persistenceId, "ambiguous-snapshot-seeder", "ambiguous-child-delivery-snapshot");

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("ambiguous-snapshot-session");
        var joined = await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);

        Assert.Equal(0, joined.TurnCount);
        Assert.Equal(0, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);

        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue after the quarantined legacy snapshot."
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);
        Assert.DoesNotContain(_chatClient.ReceivedMessages.SelectMany(static messages => messages),
            static message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text?.Contains("The child accepted-run confirmed", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Sqlite_snapshot_cold_restore_delivers_one_prepared_child_pair()
    {
        var sessionId = new SessionId(SessionValue);
        var persistenceId = $"session-{sessionId.Value}";
        await SeedSnapshotAsync(persistenceId, "prepared-snapshot-seeder", "prepared-undelivered-child-snapshot");

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("prepared-snapshot-session");
        await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);
        await _chatClient.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var messages = Assert.Single(_chatClient.ReceivedMessages);
        var childCalls = messages.SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
            .Where(static call => call.Name == "child_run_result")
            .ToArray();
        var childResults = messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => childCalls.Any(call => call.CallId == result.CallId))
            .ToArray();
        var childCall = Assert.Single(childCalls);
        Assert.Single(childResults);
        Assert.Equal(childCall.CallId, childResults[0].CallId);
        Assert.Equal("accepted-run", childCall.Arguments?["run_id"]?.ToString());
        Assert.Contains("The child accepted-run confirmed its result.", childResults[0].Result?.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(_toolExecutor.Calls);
        Assert.Equal(1, _chatClient.CallCount);
    }

    private sealed class SingleClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }

    private sealed class CountingToolExecutor : IToolExecutor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _calls = new();

        public IReadOnlyCollection<string> Calls => _calls.ToArray();

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext context,
            CancellationToken ct = default)
        {
            _calls.Enqueue(toolCall.Name);
            return Task.FromResult("unused");
        }
    }
}
