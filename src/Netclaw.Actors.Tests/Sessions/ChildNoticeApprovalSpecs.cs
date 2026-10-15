// -----------------------------------------------------------------------
// <copyright file="ChildNoticeApprovalSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs for the security rule that an automated input never abandons an open approval.
/// A notice carries the principal of a person, because it carries the requester of the child owner. The runtime must
/// therefore classify a notice by the kind of the input and not by its principal.
/// Source: tool-authorization TA-11 ("The wait SHALL end only with an answer, a cancellation, or a new user message"),
/// the D1 scenario "A child result does not abandon the approval", and <c>s2/DESIGN.md</c> S2-F1.
/// </summary>
public sealed class ChildNoticeApprovalSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// Spec 8. A recovered session holds a parked prompt of Alice. Alice's child ends at the recovery. The prompt stays
    /// open, and the notice runs after Alice answers.
    /// Planted bug: the abandonment plan classifies an input by its principal only, as the D1 code does today.
    /// </summary>
    [Fact]
    public async Task A_notice_never_abandons_an_open_parent_approval()
    {
        // Arrange
        var sessionId = new SessionId("notice/keeps-approval");
        var journal = await JournalAsync();
        ScriptParent();
        var first = await JoinAsync(sessionId);
        var (accepted, _) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(first, 1);
        await SendAsync(sessionId, "probe:p1", Operator(Alice));
        var prompt = await FishApprovalAsync(first, "probe-p1");

        // Act: the crash ends the child. The recovery records a lost child and parks the prompt of Alice.
        await RestartSessionAsync(sessionId);
        await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, accepted.Run.RunId),
            "The recovery must produce the result of the lost child."), Deadline, cancellationToken: Cancellation);

        // Assert: the prompt is open, and the result waits for the answer.
        AssertNoAbandonment(journal);
        Assert.Empty(ApprovalTool.Contexts);
        var requestsBeforeAnswer = Clients.Parent.ReceivedMessages.Count;
        AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ToolCallRecorded>(),
            evt => evt.ToolResult.ToolCallId?.Value == "probe-p1"
                   && evt.ToolResult.Content?.StartsWith("confirmed", StringComparison.Ordinal) == true),
            Deadline, cancellationToken: Cancellation);
        Assert.Single(ApprovalTool.Contexts);
        AssertNoAbandonment(journal);
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages, request => CarriesNoticeFor(request, accepted.Run.RunId)) >= 0,
            "The notice must run after the answer."), Deadline, cancellationToken: Cancellation);
        var noticeIndex = IndexOf(Clients.Parent.ReceivedMessages, request => CarriesNoticeFor(request, accepted.Run.RunId));
        Assert.True(noticeIndex >= requestsBeforeAnswer,
            $"The notice reached model request {noticeIndex}, before the answer ({requestsBeforeAnswer} requests).");
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
    }

    /// <summary>
    /// Spec 16. A notice waits behind a parked prompt. The session passivates and recovers a second time. The rebuilt
    /// waiting notice keeps its kind. It does not abandon the prompt, and a later answer of Alice runs the tool first.
    /// Planted bug: the rebuild of the waiting inputs forgets the kind of the notice.
    /// </summary>
    [Fact]
    public async Task A_rebuilt_waiting_notice_keeps_its_kind()
    {
        // Arrange: the first recovery admits the notice of the lost child behind the parked prompt.
        var sessionId = new SessionId("notice/rebuilt-kind");
        var journal = await JournalAsync();
        ScriptParent();
        var first = await JoinAsync(sessionId);
        var (accepted, _) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(first, 1);
        await SendAsync(sessionId, "probe:p1", Operator(Alice));
        var prompt = await FishApprovalAsync(first, "probe-p1");
        await RestartSessionAsync(sessionId);
        await AwaitAssertAsync(() => Assert.True(NoticesFor(journal, accepted.Run.RunId).Count == 1,
            "The recovery must admit one notice for the lost child."), Deadline, cancellationToken: Cancellation);
        AssertNoAbandonment(journal);

        // Act: the idle session passivates with the prompt open and the notice waiting, and then it recovers.
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        session.Tell(ReceiveTimeout.Instance);
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        await JoinAsync(sessionId);

        // Assert
        AssertNoAbandonment(journal);
        Assert.Empty(ApprovalTool.Contexts);
        var requestsBeforeAnswer = Clients.Parent.ReceivedMessages.Count;
        AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));
        await AwaitAssertAsync(() => Assert.Single(ApprovalTool.Contexts), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages, request => CarriesNoticeFor(request, accepted.Run.RunId)) >= requestsBeforeAnswer,
            "The notice must run after the answer."), Deadline, cancellationToken: Cancellation);
        AssertNoAbandonment(journal);
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
    }

    private static void AssertNoAbandonment(AcceptanceFault journal)
    {
        var abandoned = journal.Committed.OfType<ToolBatchAbandoned>().ToArray();
        Assert.True(abandoned.Length == 0,
            "A notice must not abandon the open approval. Abandoned: "
            + string.Join(" | ", abandoned.SelectMany(evt => evt.ToolResults.Select(result => result.Content))));
    }
}
