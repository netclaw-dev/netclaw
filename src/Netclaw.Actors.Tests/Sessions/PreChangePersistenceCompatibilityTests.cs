// -----------------------------------------------------------------------
// <copyright file="PreChangePersistenceCompatibilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class PreChangePersistenceCompatibilityTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(20);
    private readonly FakeChatClient _client = new();
    private readonly ApprovalGateToolExecutor _executor = new();
    protected override bool VerifySerialization => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning { TitleGenerationInterval = 0, SnapshotInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Continue a neutral task."));
        services.AddSingleton<IToolExecutor>(new ReceiptToolExecutor(_executor));
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create((string command) => command, "shell_execute"), "builtin");
        registry.RegisterCore(AIFunctionFactory.Create((string path) => path, "read_file"), "builtin");
        services.AddSingleton(registry);
        _executor.GatedTools.Add("shell_execute");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_completed_task_recovers_without_a_model_or_effect_replay(bool useSnapshot)
    {
        var fixture = await ReadFixtureAsync();
        var session = new SessionId(fixture.SessionId);
        var input = Decode<InputAdmitted>(fixture, "input");
        AssertOriginalAuthority(input);
        var turn = Decode<TurnRecorded>(fixture, "completed-turn");
        Assert.Equal(input.InputId, Assert.Single(turn.ConsumedInputIds));
        await SeedAsync(fixture, session, useSnapshot, parked: false);
        var subscriber = CreateTestProbe();
        var joined = await JoinSessionAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, session);
        Assert.Equal(1, joined.TurnCount);
        Assert.Equal(0, _client.CallCount);
        Assert.Equal(0, _executor.SuccessfulExecutions);
        var snapshot = Decode<SessionSnapshot>(fixture, "completed-snapshot");
        Assert.Empty(snapshot.PendingInputs);
        Assert.Contains(snapshot.History, message => message.Content == "The neutral task is complete.");

        await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Answer a new neutral question.",
            Source = new Netclaw.Actors.Channels.MessageSource
            {
                ChannelType = Netclaw.Actors.Channels.ChannelType.SignalR, SenderId = new SenderId("operator-a"),
                MessageId = "after-upgrade", TurnId = new TurnId("after-upgrade"), Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
            }
        }, FaultCeiling, TestContext.Current.CancellationToken);
        var complete = await subscriber.FishForMessageAsync<TurnCompleted>(static _ => true,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, complete.Outcome);
        Assert.Equal(1, _client.CallCount);
        Assert.Equal(0, _executor.SuccessfulExecutions);
        var messages = Assert.Single(_client.ReceivedMessages);
        Assert.Contains(messages, message => message.Text == input.UserMessage.Content);
        Assert.Contains(messages, message => message.Text == "The neutral task is complete.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_parked_prompt_keeps_authority_and_does_not_reexecute_its_completed_sibling(bool useSnapshot)
    {
        var fixture = await ReadFixtureAsync();
        var session = new SessionId(fixture.SessionId);
        var prompt = Decode<ToolApprovalRequested>(fixture, "approval-prompt");
        var input = Decode<InputAdmitted>(fixture, "parked-input");
        AssertOriginalAuthority(input);
        Assert.NotNull(prompt.TurnContext);
        Assert.Equal(input.TurnContext.TurnId, prompt.TurnContext.TurnId);
        Assert.Equal(input.TurnContext.RequesterSenderId, prompt.TurnContext.RequesterSenderId);
        Assert.Equal("auth-00000000000040008000000000000001", prompt.AuthorizationAttemptId);
        Assert.Equal(new[] { ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny }, prompt.OptionKeys);
        await SeedAsync(fixture, session, useSnapshot, parked: true);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full);

        var denied = await manager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = session, CallId = new ToolCallId("legacy-protected"),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce), SenderId = new SenderId("operator-b")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        Assert.IsType<CommandNack>(denied);
        Assert.Equal(0, _client.CallCount);
        Assert.Equal(0, _executor.SuccessfulExecutions);

        var accepted = await manager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = session, CallId = new ToolCallId("legacy-protected"),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce), SenderId = new SenderId("operator-a")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(accepted);
        var complete = await subscriber.FishForMessageAsync<TurnCompleted>(static _ => true,
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, complete.Outcome);
        Assert.Equal(0, _executor.ExecutionsFor("read_file"));
        Assert.Equal(1, _executor.ExecutionsFor("shell_execute"));
        Assert.Equal(TrustAudience.Personal, _executor.LastExecutionAudience);
        Assert.Equal(TrustBoundary.Personal, _executor.LastExecutionBoundary);
        Assert.Equal("signalr", _executor.LastExecutionChannelType);
        Assert.True(_executor.LastSupportsInteractiveApproval);
        Assert.Contains(_executor.AuthorizationAttempts, attempt => attempt.Value == "auth-00000000000040008000000000000001");
        var request = Assert.Single(_client.ReceivedMessages);
        var sibling = Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            result => result.CallId == "legacy-read");
        Assert.Equal("neutral-sibling-result", Assert.IsType<string>(sibling.Result));
        Assert.Contains(request, message => message.Text == "The neutral task is complete.");

        var duplicate = await manager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = session, CallId = new ToolCallId("legacy-protected"),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce), SenderId = new SenderId("operator-a")
        }, FaultCeiling, TestContext.Current.CancellationToken);
        Assert.IsType<CommandNack>(duplicate);
        Assert.Equal(1, _executor.SuccessfulExecutions);
    }

    private static void AssertOriginalAuthority(InputAdmitted input)
    {
        Assert.Equal(TrustAudience.Personal, input.TurnContext.Audience);
        Assert.Equal(TrustBoundary.Personal, input.TurnContext.Boundary);
        Assert.Equal(new SenderId("operator-a"), input.TurnContext.RequesterSenderId);
        Assert.Equal(PrincipalClassification.Operator, input.TurnContext.RequesterPrincipal);
        Assert.Equal(TransportAuthenticity.Verified, input.TurnContext.TransportAuthenticity);
        Assert.Equal(PayloadTaint.Trusted, input.TurnContext.PayloadTaint);
        Assert.Equal("operator-a", input.TurnContext.DefaultDeliveryTarget?.DestinationId);
        Assert.True(input.TurnContext.SupportsInteractiveApproval);
    }

    private async Task SeedAsync(CapturedFixture fixture, SessionId session, bool useSnapshot, bool parked)
    {
        var seed = Sys.ActorOf(Props.Create(() => new FixtureSeeder($"session-{session.Value}")));
        await seed.Ask<Done>(Decode<InputAdmitted>(fixture, "input"), FaultCeiling, TestContext.Current.CancellationToken);
        await seed.Ask<Done>(Decode<TurnRecorded>(fixture, "completed-turn"), FaultCeiling, TestContext.Current.CancellationToken);
        if (useSnapshot)
            await seed.Ask<Done>(Decode<SessionSnapshot>(fixture, "completed-snapshot"), FaultCeiling, TestContext.Current.CancellationToken);
        if (parked)
        {
            foreach (var name in new[] { "parked-input", "batch", "sibling-result", "approval-prompt" })
                await seed.Ask<Done>(Decode<ISessionEvent>(fixture, name), FaultCeiling, TestContext.Current.CancellationToken);
        }
        Watch(seed);
        Sys.Stop(seed);
        await ExpectTerminatedAsync(seed, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
    }

    private T Decode<T>(CapturedFixture fixture, string name)
    {
        var entry = fixture.Entries[name];
        var bytes = Convert.FromBase64String(entry.Base64);
        Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        return Assert.IsAssignableFrom<T>(Sys.Serialization.Deserialize(bytes, entry.SerializerId, entry.Manifest));
    }

    private static async Task<CapturedFixture> ReadFixtureAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LegacyPersistence", "legacy-session-v0.json");
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var fixture = JsonSerializer.Deserialize<CapturedFixture>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The legacy fixture is empty.");
        Assert.Equal("2e6bc4f014dc96b606566709df1bd11f90ecf34a", fixture.Baseline);
        Assert.Equal(7, fixture.Entries.Count);
        Assert.All(fixture.Entries.Values, entry => Assert.Equal(150, entry.SerializerId));
        return fixture;
    }

    private sealed record CapturedFixture(string Baseline, string SessionId, Dictionary<string, CapturedEntry> Entries);
    private sealed record CapturedEntry(int SerializerId, string Manifest, string Base64, string Sha256);

    private sealed class ReceiptToolExecutor(ApprovalGateToolExecutor executor) : IToolExecutor
    {
        public Task AuthorizeAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
            => executor.AuthorizeAsync(call, context, ct);

        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            var result = await executor.ExecuteAsync(call, context, ct);
            if (!context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null)))
                throw new InvalidOperationException("The fake executor already emitted a dispatch receipt.");
            return result;
        }
    }

    private sealed class FixtureSeeder : ReceivePersistentActor
    {
        private IActorRef? _snapshotReply;
        public override string PersistenceId { get; }
        public FixtureSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });
            Command<ISessionEvent>(evt => { var reply = Sender; Persist(evt, _ => reply.Tell(Done.Instance)); });
            Command<SessionSnapshot>(snapshot => { _snapshotReply = Sender; SaveSnapshot(snapshot); });
            Command<SaveSnapshotSuccess>(_ =>
                (_snapshotReply ?? throw new InvalidOperationException("No snapshot request exists.")).Tell(Done.Instance));
            Command<SaveSnapshotFailure>(failure =>
                (_snapshotReply ?? throw new InvalidOperationException("No snapshot request exists.")).Tell(new Status.Failure(failure.Cause)));
        }
    }
}
