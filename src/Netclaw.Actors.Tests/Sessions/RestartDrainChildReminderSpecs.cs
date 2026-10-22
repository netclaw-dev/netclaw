// -----------------------------------------------------------------------
// <copyright file="RestartDrainChildReminderSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for issue #2601: a restart reminder that exists only to report a child run does not resume the
/// stopped turn.
/// <para>
/// <c>MarkRestartReminderEligible</c> returns <c>Eligible</c> when a child run owes its result, also when no input of
/// the stopped turn stays pending. That reminder wakes the session only to report the child. The stopped turn does not
/// resume, so the session must post the notice.
/// </para>
/// </summary>
public sealed class RestartDrainChildReminderSpecs : ChildNoticeTestBase
{
    public RestartDrainChildReminderSpecs(ITestOutputHelper output) : base(output)
    {
    }

    protected override bool UseTestScheduler => true;

    [Fact]
    public async Task A_drain_in_a_tool_loop_tells_the_user_when_the_only_reminder_reports_a_child()
    {
        // Arrange: Alice has a live child. Her next turn reads a project file, and the model call after the read
        // waits until the drain stops it.
        var sessionId = new SessionId("notice/restart-tool-loop-child-only");
        var projectFile = Path.Combine(TestPaths.WorkspacesDirectory, "project", "plan.md");
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        await File.WriteAllTextAsync(projectFile, "The plan.", Cancellation);
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        var hold = NewGate();
        var entered = ParentGate.Arm(
            request => Results(request).Any(result => result.CallId.StartsWith("read-project-", StringComparison.Ordinal)),
            hold);
        await SendAsync(sessionId, $"read:{projectFile}", Operator(Alice));
        await entered.WaitAsync(Deadline, Cancellation);
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);

        // Act: the drain gives the model call 2 seconds. The spec moves the virtual clock until the drain replies.
        var drain = AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "config-reload"));
        await AwaitAssertAsync(() =>
        {
            AdvanceScheduler(TimeSpan.FromSeconds(3));
            Assert.True(drain.IsCompleted, "The restart drain did not finish.");
        }, TimeSpan.FromSeconds(5), cancellationToken: Cancellation);
        var prepared = await drain;

        // Assert: the reminder exists only to report the child. The stopped turn does not resume, so the user gets
        // the notice before the session stops.
        Assert.NotNull(prepared.RestartReminder);
        var output = await subscriber.FishForMessageAsync(
            message => message is SessionDeactivated || IsRestartNotice(message),
            Deadline,
            cancellationToken: Cancellation);
        Assert.True(IsRestartNotice(output),
            "The restart reminder only reports the child, and the stopped turn does not resume, but the session gave the user no notice.");
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
    }

    private static bool IsRestartNotice(object message)
        => message is TextOutput text && text.Text.Contains("restart", StringComparison.OrdinalIgnoreCase);
}
