// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.SpawnFailure.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Fact]
    public async Task A_synchronous_child_creation_failure_retains_one_acceptance_and_one_spawn_error()
    {
        // Akka resolves the named mailbox inside ActorOf, before it returns a child reference.
        _start!.ChildMailbox = "unconfigured-child-owner-mailbox";
        Assert.False(Sys.Settings.Config.HasPath(_start.ChildMailbox));
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        ChildRunAccepted? accepted = null;
        await EventFilter.Error(contains: "Child creation failed after acceptance").ExpectOneAsync(async () =>
        {
            await SendOriginalAsync(manager);
            await AwaitAssertAsync(async () =>
            {
                var records = await ReadJournalAsync();
                accepted = Assert.Single(records.OfType<ChildRunAccepted>());
                Assert.Empty(records.OfType<ChildRunEvent.Started>());
                var terminal = Assert.Single(records.OfType<ChildRunEvent.TerminalRecorded>());
                Assert.Equal(accepted.Run.RunId, terminal.RunId);
                Assert.False(terminal.Terminal.Result.Success);
                Assert.False(terminal.Terminal.Lost);
                Assert.Equal(SubAgentOutcomeReason.SpawnError, terminal.Terminal.Result.OutcomeReason);
                Assert.IsType<ChildRunCompletion.Failed>(terminal.Terminal.Result.Completion);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        await CompletedAsync(subscriber);
        Assert.Equal(0, _child.CallCount);
        var repeated = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(Retry(accepted), Ceiling,
            TestContext.Current.CancellationToken));
        Assert.Equal(accepted.Run.RunId, repeated.Run.RunId);
        Assert.Equal(accepted.Run.StartKey, repeated.Run.StartKey);
        Assert.Equal(accepted.Run.ArgumentsDigest, repeated.Run.ArgumentsDigest);
        Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, repeated.Run.OriginalContext));
        Assert.Equal(BackgroundChildState.Failed, repeated.Run.State);
        Assert.Equal(SubAgentOutcomeReason.SpawnError, repeated.Run.Terminal!.Result.OutcomeReason);
        var after = await ReadJournalPositionsAsync();
        Assert.Single(after, record => record.Event is ChildRunAccepted);
        Assert.DoesNotContain(after, record => record.Event is ChildRunEvent.Started);
        var terminalRow = Assert.Single(after, record => record.Event is ChildRunEvent.TerminalRecorded);
        var terminalEvent = Assert.IsType<ChildRunEvent.TerminalRecorded>(terminalRow.Event);
        Assert.Equal(terminalRow.SequenceNr, terminalEvent.TerminalSequenceNr);
        Assert.Equal(SubAgentOutcomeReason.SpawnError, terminalEvent.Terminal.Result.OutcomeReason);
        Assert.Equal(0, _child.CallCount);
    }
}
