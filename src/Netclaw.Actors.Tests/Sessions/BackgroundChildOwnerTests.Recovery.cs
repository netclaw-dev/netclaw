// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerTests.Recovery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerTests
{
    [Fact]
    public async Task Recovery_commits_two_lost_terminals_at_distinct_actual_journal_positions_before_join()
    {
        var session = new SessionId("signalr/background-recovery");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(session.Value)}")
            .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
        _main.ToolCallsOnFirstCall = new[] { "start-1", "start-2" }.Select(id => new FunctionCallContent(id, "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate a distinct fixture." })).ToList();
        var held = NewSignal();
        var release = NewSignal();
        var starts = 0;
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not ChildRunEvent.Started || Interlocked.Increment(ref starts) != 2)
                return false;
            held.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        });
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = session, Content = "Start two fixture tasks.", Source = new MessageSource
                {
                    ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"),
                    TurnId = new TurnId("original"), MessageId = "original", Audience = TrustAudience.Personal,
                    Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
                    Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
                }
            }, Ceiling, TestContext.Current.CancellationToken);
            await held.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var watcher = CreateTestProbe();
            watcher.Watch(owner);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            release.TrySetResult();
            var terminals = new ConcurrentQueue<(ChildRunEvent.TerminalRecorded Event, long Sequence)>();
            await Journal.OnWrite.FailIf(record =>
            {
                if (record.Payload is ChildRunEvent.TerminalRecorded terminal)
                    terminals.Enqueue((terminal, record.SequenceNr));
                return false;
            });
            var recovered = CreateTestProbe();
            manager.Tell(new JoinSession(recovered) { SessionId = session, Filter = OutputFilter.Full }, recovered.Ref);
            await recovered.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, terminals.Count);
            var committed = terminals.ToArray();
            Assert.All(committed, pair =>
            {
                Assert.True(pair.Event.Terminal.Lost);
                Assert.Equal(SubAgentOutcomeReason.OwnerRestartLost, pair.Event.Terminal.Result.OutcomeReason);
                Assert.Equal(pair.Sequence, pair.Event.TerminalSequenceNr);
            });
            Assert.Equal(2, committed.Select(pair => pair.Sequence).Distinct().Count());
            Assert.Equal(1, _child.CallCount);
            await AwaitAssertAsync(() =>
            {
                var results = _main.ReceivedMessages.Last().SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
                Assert.Single(results, result => result.CallId == "start-1");
                Assert.Single(results, result => result.CallId == "start-2");
                var deliveries = results.Where(result => result.CallId.StartsWith("child-result-", StringComparison.Ordinal)).ToArray();
                Assert.Equal(2, deliveries.Length);
                Assert.All(deliveries, result =>
                {
                    using var body = JsonDocument.Parse(result.Result!.ToString()!);
                    Assert.Equal("Lost", body.RootElement.GetProperty("state").GetString());
                });
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(2, terminals.Count);
        }
        finally { release.TrySetResult(); }
    }
}
