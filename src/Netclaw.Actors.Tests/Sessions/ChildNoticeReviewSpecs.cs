// -----------------------------------------------------------------------
// <copyright file="ChildNoticeReviewSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// What the session does with a child result when it admits the notice: it reviews the findings of the child, it merges
/// the reviewed working context of the child into its own, and it keeps the runtime-written run files out of the recent
/// files. The reducer tests cover the state change. These specs cover the wiring in the session actor.
/// </summary>
public sealed class ChildNoticeReviewSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    protected override bool ChildEmitsFindings => true;

    /// <summary>
    /// Review p1. A child ends with a finding. The session reviews that finding when it admits the notice, and it
    /// tells the subscribers the memory decision.
    /// Planted bug: the review reads the run after the notice marked it as reported, so it sees no result and the
    /// findings never reach the memory review.
    /// </summary>
    [Fact]
    public async Task The_findings_of_a_successful_child_are_reviewed_when_its_notice_is_admitted()
    {
        // Arrange
        var sessionId = new SessionId("notice/review-findings");
        await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (_, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        // Act
        gate.TrySetResult();

        // Assert: the memory decision reaches the subscribers.
        var reviewed = await subscriber.FishForMessageAsync<SubAgentOutput>(
            output => output.MemoryDecision is not null, Deadline, cancellationToken: Cancellation);
        Assert.Equal("child-a", reviewed.AgentName.Value);
        Assert.Equal(1, reviewed.FindingsCount);
    }

    /// <summary>
    /// Review p2. A child changes a file and ends. The reviewed working context of the child reaches the notice, and the
    /// session merges the changed file into its own recent files. The next model request shows it.
    /// Planted bug: the session builds the notice without the reviewed working context of the child.
    /// </summary>
    [Fact]
    public async Task The_reviewed_working_context_of_a_child_reaches_the_parent_with_the_notice()
    {
        // Arrange
        var sessionId = new SessionId("notice/review-context");
        var changed = Path.Combine(TestPaths.WorkspacesDirectory, "project", "ChangedByChild.cs");
        await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (_, gate) = await StartChildAsync(sessionId, Alice, "one", changedFile: changed);
        await ExpectTurnsAsync(subscriber, 1);

        // Act
        gate.TrySetResult();
        await ExpectTurnsAsync(subscriber, 1);
        await SendAsync(sessionId, "ping", Operator(Alice));
        await ExpectTurnsAsync(subscriber, 1);

        // Assert
        var recentFiles = RecentFilesOf(Clients.Parent.ReceivedMessages.Last());
        Assert.True(recentFiles.Contains(changed), $"recent_files: [{string.Join(", ", recentFiles)}]. Expected: {changed}");
    }

    /// <summary>
    /// The model reads <c>result.md</c> and <c>task.md</c> of a run, and the runtime wrote both files. Those reads must
    /// not enter the recent files: the list holds ten entries, and a result read would push a project file out of it.
    /// A project file stays in the list, even when its name is <c>result.md</c>.
    /// Planted bug: the session records every file read in the recent files.
    /// </summary>
    [Fact]
    public async Task A_read_of_a_child_run_file_stays_out_of_the_recent_files_and_a_project_file_stays_in()
    {
        // Arrange
        var sessionId = new SessionId("notice/review-recent-files");
        var projectFile = Path.Combine(TestPaths.WorkspacesDirectory, "project", "App.cs");
        var projectResult = Path.Combine(TestPaths.WorkspacesDirectory, "project", "result.md");
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        await File.WriteAllTextAsync(projectFile, "class App {}", Cancellation);
        await File.WriteAllTextAsync(projectResult, "A project file that is named like a result file.", Cancellation);
        await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        // Act: the notice turn reads result.md. Then the model reads task.md, a project file, and a project result.md.
        gate.TrySetResult();
        await ExpectTurnsAsync(subscriber, 1);
        var runResult = ResultPath(sessionId, accepted);
        var runTask = TaskPath(sessionId, accepted);
        Assert.True(File.Exists(runResult) && File.Exists(runTask), "The runtime must write task.md and result.md.");
        foreach (var path in new[] { runTask, projectFile, projectResult })
        {
            await SendAsync(sessionId, $"read:{path}", Operator(Alice));
            await ExpectTurnsAsync(subscriber, 1);
        }

        await SendAsync(sessionId, "ping", Operator(Alice));
        await ExpectTurnsAsync(subscriber, 1);

        // Assert
        Assert.True(Clients.Parent.ReceivedMessages.Any(request => Results(request).Any(result =>
                result.Result?.ToString()?.Contains("class App", StringComparison.Ordinal) == true)),
            "The model must have read the project file.");
        var recentFiles = RecentFilesOf(Clients.Parent.ReceivedMessages.Last());
        Assert.True(recentFiles.Contains(projectFile) && recentFiles.Contains(projectResult),
            $"recent_files: [{string.Join(", ", recentFiles)}]. Expected: {projectFile} and {projectResult}");
        Assert.DoesNotContain(runResult, recentFiles);
        Assert.DoesNotContain(runTask, recentFiles);
    }

    // The recent files that the session shows the model in its working-context block.
    private static string[] RecentFilesOf(IReadOnlyList<ChatMessage> request)
        => request
            .Where(message => message.Text.Contains("[working-context]", StringComparison.Ordinal))
            .SelectMany(message => message.Text.Split('\n'))
            .Where(line => line.StartsWith("  - ", StringComparison.Ordinal))
            // The volatile block sits in brackets, so its last line ends with the closing bracket.
            .Select(line => line["  - ".Length..].Trim().TrimEnd(']'))
            .ToArray();
}
