// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnIntegrationTests.AcceptanceReplies.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public partial class SubAgentSpawnIntegrationTests
{
    [Theory]
    [InlineData("requester")]
    [InlineData("input")]
    [InlineData("operation")]
    public async Task The_actual_slash_adapter_rejects_a_coherent_reply_for_another_stamped_activation(string defect)
    {
        var ceiling = TimeSpan.FromSeconds(10);
        var session = new SessionId("test-channel/slash-acceptance-correlation");
        const string mailbox = "slash-acceptance-reply-gate";
        Sys.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            $"{mailbox} {{ mailbox-type = \"{typeof(BackgroundChildApprovalGrantAdversarialTests.WaitReplyMailbox).AssemblyQualifiedName}\" }}"));
        ((ExtendedActorSystem)Sys).Provider.Deployer.SetDeploy(new Deploy(
            $"/session-manager/{Uri.EscapeDataString(session.Value)}", Config.Empty, NoRouter.Instance, LocalScope.Instance,
            Deploy.NoDispatcherGiven, mailbox));
        var gate = BackgroundChildApprovalGrantAdversarialTests.WaitReplyMailbox.Gates.GetOrCreateValue(Sys);
        gate.MessageName = nameof(StartBackgroundChildRun);
        gate.Arm();
        try
        {
            var manager = ActorRegistry.Get<SessionManagerActorKey>();
            var subscriber = CreateTestProbe();
            await JoinSessionAsync(manager, subscriber, session, OutputFilter.Full | OutputFilter.ProcessingState);
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = session, Content = "/ops-route inspect the neutral fixture", Source = BuildPersonalSource()
            }, ceiling, TestContext.Current.CancellationToken);
            var captured = await gate.Held.Task.WaitAsync(ceiling, TestContext.Current.CancellationToken);
            var request = Assert.IsType<StartBackgroundChildRun>(captured.Envelope.Message);
            var activation = Assert.IsType<ChildRunStartKey.Slash>(request.StartKey);
            Assert.Equal(session, activation.SessionId);
            Assert.Equal("/ops-route", request.SourceOperation);
            var id = new SubAgentRunId("recorded-slash-run");
            var run = new BackgroundChildRun
            {
                RunId = id, AgentName = request.Prepared.AgentName,
                ScopeId = new SubAgentScopeId($"{session.Value}/subagent/{request.Prepared.AgentName.Value}/{id.Value}"),
                ArgumentsDigest = request.Prepared.ArgumentsDigest, StartKey = request.StartKey,
                SourceOperation = request.SourceOperation, OriginalContext = request.InvocationContext,
                OriginInputIds = [activation.InputId], InitialWorkingSnapshot = request.Prepared.Execution.Scope.InitialWorkingSnapshot,
                ParentCheckpoint = new ToolLoopCheckpoint { TaskId = "original-slash-task" },
                StartBatchSettled = true, AcceptedAtMs = 1, StartedAtMs = 2
            };
            run = defect switch
            {
                "requester" => run with { OriginalContext = run.OriginalContext with { RequesterSenderId = new SenderId("foreign-requester") } },
                "input" => run with { StartKey = new ChildRunStartKey.Slash(new InputId("foreign-input"))
                    { SessionId = session, TurnId = activation.TurnId }, OriginInputIds = [new InputId("foreign-input")] },
                "operation" => run with { SourceOperation = "/other-route" },
                _ => throw new ArgumentOutOfRangeException(nameof(defect))
            };
            // The held owner request cannot execute. Its Ask sender still exercises the actual slash reply closure.
            run.Validate();
            captured.Envelope.Sender.Tell(new ChildStartReply.Accepted(run, BackgroundChildState.Running), captured.Receiver);
            var outputs = new List<object>();
            await subscriber.FishForMessageAsync<object>(message =>
            {
                outputs.Add(message);
                return message is ProcessingStateOutput { IsProcessing: false } && outputs.OfType<TurnCompleted>().Any();
            }, ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var failure = Assert.Single(outputs.OfType<ErrorOutput>());
            Assert.Equal(ErrorCategory.ToolFailure, failure.Category);
            Assert.Contains("routed to subagent", failure.Message, StringComparison.Ordinal);
            Assert.Equal(TurnOutcome.Failed, Assert.Single(outputs.OfType<TurnCompleted>()).Outcome);
            Assert.Empty(outputs.OfType<TextOutput>());
            Assert.Empty(outputs.OfType<SubAgentOutput>());
            Assert.Equal(0, _clientProvider.Main.CallCount);
            Assert.Equal(0, _clientProvider.Compaction.CallCount);
            var identity = await Sys.ActorSelection($"{captured.Receiver.Path}/child-run-{request.Prepared.RunId.Value}")
                .Ask<ActorIdentity>(new Identify("no-uncommitted-child"), ceiling, TestContext.Current.CancellationToken);
            Assert.Null(identity.Subject);
        }
        finally { gate.Release(); }
    }
}
