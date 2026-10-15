// -----------------------------------------------------------------------
// <copyright file="WaitingReminderIdleAbandonmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: a reminder that waits behind a tool batch must survive the idle abandonment of that batch.
/// <para>
/// A model reply holds two gated tool calls, so the batch has two open approvals. The person answers one of them.
/// A reminder arrives and waits. The person leaves. The idle timeout abandons the half-answered batch and the session
/// passivates. The session acknowledged the reminder, so the reminder manager does not send it again. The journal is the
/// only copy of the reminder. The reminder must run once, under its own authority, after the session wakes.
/// </para>
/// </summary>
public sealed class WaitingReminderIdleAbandonmentTests : ApprovalWaitTestBase
{
    private const string FirstCallId = "call-shell-first";
    private const string SecondCallId = "call-read-second";
    private const string WakeText = "Wake the session.";

    public WaitingReminderIdleAbandonmentTests(ITestOutputHelper output) : base(output)
    {
    }

    public enum WakeBy
    {
        // A subscriber joins the passivated session.
        Join,

        // A person sends a message to the passivated session.
        PersonInput,
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        // The last registration wins. The batch needs a second tool, and the base registers only one.
        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        registry.Register(
            AIFunctionFactory.Create((string path) => $"read {path}", "read_file"),
            "read_file");
        services.AddSingleton(registry);
    }

    [Theory]
    [InlineData(WakeBy.Join)]
    [InlineData(WakeBy.PersonInput)]
    public async Task A_waiting_reminder_runs_once_after_idle_abandons_a_half_answered_batch(WakeBy wakeBy)
    {
        // Arrange: two open approvals, a cold recovery, and a reminder that waits behind them.
        var sessionId = new SessionId($"test-channel/idle-abandons-half-answered-batch-{wakeBy}");
        await ParkTwoApprovalsAsync(sessionId);
        await StopSessionAsync(sessionId);
        await JoinNewSubscriberAsync(sessionId);
        await SendReminderAsync(sessionId, $"build-check:1700000000100:{wakeBy}");

        // The person answers one approval. The other stays open, so the batch cannot continue.
        var reply = await SessionManager.Ask<ISessionResponse>(
            AnswerFor(sessionId, FirstCallId, approve: true),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);

        // Act: the person leaves. The spec sends the idle message itself, so no timer races it.
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        if (wakeBy == WakeBy.Join)
            await JoinNewSubscriberAsync(sessionId);
        else
            await SendAsync(sessionId, WakeText, RequesterSource(Requester));

        // Assert: the reminder runs once, as its own turn under its own authority.
        await AwaitRequestAsync(ReminderText);
        if (wakeBy == WakeBy.PersonInput)
            await AwaitRequestAsync(WakeText);
        await AssertNoInputStaysPendingAsync(sessionId);
        await AssertNoFurtherRunAsync(sessionId);
        AssertRanOnce(ReminderText, ReminderPromptMarker);
        if (wakeBy == WakeBy.PersonInput)
            AssertRanOnce(WakeText, RequesterPromptMarker);
    }

    // Control: the same wake after a plain answer. One approval closes, the reminder runs, and the idle abandonment
    // finds nothing to drop. Passivation and reactivation must not run the reminder a second time.
    [Fact]
    public async Task A_reminder_that_ran_behind_one_approval_does_not_run_again_after_passivation()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/idle-after-reminder-ran-once");
        await ParkApprovalThenRecoverAsync(sessionId);
        await SendReminderAsync(sessionId, "build-check:1700000000101");
        var reply = await SessionManager.Ask<ISessionResponse>(
            AnswerFor(sessionId, CallId, approve: true),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);
        await AwaitRequestAsync(ReminderText);
        await AssertNoInputStaysPendingAsync(sessionId);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);

        // Act
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        await JoinNewSubscriberAsync(sessionId);

        // Assert
        await AssertNoFurtherRunAsync(sessionId);
        AssertRanOnce(ReminderText, ReminderPromptMarker);
        AssertReminderRanOnceAfterApproval();
    }

    private async Task ParkTwoApprovalsAsync(SessionId sessionId)
    {
        ToolExecutor.GatedTools.Add("shell_execute");
        ToolExecutor.GatedTools.Add("read_file");
        FakeChat.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(FirstCallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" }),
            new FunctionCallContent(SecondCallId, "read_file",
                new Dictionary<string, object?> { ["path"] = "README.md" })
        ];

        var first = CreateTestProbe("first-subscriber");
        await JoinAsync(first, sessionId, OutputFilter.Full);
        await SendAsync(sessionId, "Run git status and read the file", RequesterSource(Requester));
        for (var call = 0; call < 2; call++)
        {
            await first.ExpectMsgAsync<ToolCallOutput>(
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }

        for (var call = 0; call < 2; call++)
        {
            await first.ExpectMsgAsync<ToolInteractionRequest>(
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static ToolInteractionResponse AnswerFor(SessionId sessionId, string callId, bool approve)
        => new()
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(approve ? ApprovalOptionKeys.ApproveOnce : ApprovalOptionKeys.Deny),
            SenderId = new SenderId(Requester)
        };

    private async Task AwaitRequestAsync(string userText)
        => await AwaitAssertAsync(
            () =>
            {
                Assert.Contains(FakeChat.ReceivedMessages, request => LastUserText(request) == userText);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

    // A subscriber that joins after the run sees only the output of a later run. A second run of the same input is
    // the failure this guards against.
    private async Task AssertNoFurtherRunAsync(SessionId sessionId)
    {
        var late = await JoinNewSubscriberAsync(sessionId);
        await late.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
    }

    private void AssertRanOnce(string userText, string promptMarker)
    {
        var requests = FakeChat.ReceivedMessages;
        var run = Assert.Single(requests, request => LastUserText(request) == userText);
        Assert.Contains(promptMarker, SystemText(run));
        Assert.All(requests, request => Assert.True(
            UserTexts(request).Count(text => text == userText) <= 1,
            $"The input '{userText}' must not appear twice in one model request."));
    }
}
