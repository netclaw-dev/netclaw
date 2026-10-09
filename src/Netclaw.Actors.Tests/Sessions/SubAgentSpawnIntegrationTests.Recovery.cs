// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnIntegrationTests.Recovery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Routing;
using ModelChatRole = Microsoft.Extensions.AI.ChatRole;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public partial class SubAgentSpawnIntegrationTests
{
    [Fact]
    public async Task A_recovered_direct_slash_keeps_its_original_context_before_a_later_input()
    {
        var ceiling = TimeSpan.FromSeconds(10);
        var session = new SessionId("test-channel/recovered-direct-slash");
        Sys.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            $"slash-recovery-capture {{ mailbox-type = \"{typeof(ToolRecurrenceAdversarialTests.ReplyCaptureMailbox).AssemblyQualifiedName}\" }}"));
        ((ExtendedActorSystem)Sys).Provider.Deployer.SetDeploy(new Deploy(
            $"/session-manager/{Uri.EscapeDataString(session.Value)}", Config.Empty, NoRouter.Instance, LocalScope.Instance,
            Deploy.NoDispatcherGiven, "slash-recovery-capture"));
        MessageSource Source(string turn, string requester) => new()
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester), TurnId = new TurnId(turn), MessageId = turn,
            Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        };
        InputAdmitted Input(string id, string content, string requester) => new()
        {
            SessionId = session, InputId = new InputId(id), ExecutableText = content,
            UserMessage = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = content },
            TurnContext = TurnContext.FromMessageSource(session, new TurnId(id), Source(id, requester)).ToRecord()
        };
        var original = Input("original-command", "/ops-route inspect the original neutral task", "operator-a");
        var later = Input("later-request", "Answer the later neutral request.", "operator-b");
        var seed = Sys.ActorOf(Props.Create(() => new PendingSlashSeed($"session-{session.Value}")));
        await seed.Ask<Done>(original, ceiling, TestContext.Current.CancellationToken);
        await seed.Ask<Done>(later, ceiling, TestContext.Current.CancellationToken);
        var watcher = CreateTestProbe();
        watcher.Watch(seed);
        Sys.Stop(seed);
        await watcher.ExpectTerminatedAsync(seed, ceiling, cancellationToken: TestContext.Current.CancellationToken);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProvider.Compaction.NextResponseGate = gate;
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(ceiling, cancellationToken: TestContext.Current.CancellationToken);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Resume the work that was interrupted by the daemon restart.",
            Source = Source("restart-resume-neutral:1", "reminder-system") with
            { ReminderId = new ReminderId("restart-resume-neutral:1"), Principal = PrincipalClassification.VerifiedAutomation }
        }, ceiling, TestContext.Current.CancellationToken);
        await ExpectTurnNumberAsync(subscriber, 2, ceiling, TestContext.Current.CancellationToken);
        await _clientProvider.Compaction.FirstCallEntered.Task.WaitAsync(ceiling, TestContext.Current.CancellationToken);
        var start = Assert.IsType<StartBackgroundChildRun>(Assert.Single(
            ToolRecurrenceAdversarialTests.ReplyCaptureMailbox.Captures.GetOrCreateValue(Sys),
            envelope => envelope.Message is StartBackgroundChildRun).Message);
        Assert.Equivalent(original.TurnContext, start.InvocationContext, strict: true);
        Assert.Equal(original.InputId, Assert.IsType<ChildRunStartKey.Slash>(start.StartKey).InputId);
        Assert.Equal(original.TurnContext.TurnId, start.StartKey.TurnId.Value);
        Assert.NotEqual(later.TurnContext.TurnId, start.InvocationContext.TurnId);
        Assert.Contains(_clientProvider.Compaction.ReceivedMessages[0], message => message.Role == ModelChatRole.User
            && message.Text?.EndsWith("Task:\ninspect the original neutral task", StringComparison.Ordinal) == true);
        Assert.Contains(_clientProvider.Main.ReceivedMessages[0], message => message.Role == ModelChatRole.User
            && message.Text?.Contains("Answer the later neutral request.", StringComparison.Ordinal) == true);
        Assert.Equal(1, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);
        Assert.False(gate.Task.IsCompleted);
        gate.TrySetResult();
        await ExpectTurnNumberAsync(subscriber, 3, ceiling, TestContext.Current.CancellationToken);
        Assert.Equal(2, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);
    }

    private sealed class PendingSlashSeed : ReceivePersistentActor
    {
        public override string PersistenceId { get; }
        public PendingSlashSeed(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });
            Command<InputAdmitted>(input =>
            {
                var reply = Sender;
                Persist(input, _ => reply.Tell(Done.Instance));
            });
        }
    }
}
