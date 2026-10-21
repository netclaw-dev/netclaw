// -----------------------------------------------------------------------
// <copyright file="ChildNoticeSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs for S2: a child result reaches the parent as an admitted notice, and the model reads the result
/// file with a real <c>file_read</c> call. Source: <c>s2/DESIGN.md</c> sections 2 and 6, and the plan rules 1, 2, and 5.
/// Each test states one contract and prints the observed values when the contract fails.
/// </summary>
public sealed class ChildNoticeSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// Spec 1. After a child ends, <c>result.md</c> holds the outcome, the text, and the log path. The journal holds one
    /// admitted notice that names the agent, the run, the outcome, and the path, and never the output.
    /// Planted bug: the notice text carries the child output.
    /// </summary>
    [Fact]
    public async Task Child_terminal_writes_result_file_then_admits_one_metadata_notice()
    {
        // Arrange
        var sessionId = new SessionId("notice/result-file");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            // Act
            gate.TrySetResult();
            var resultPath = ResultPath(sessionId, accepted);
            await AwaitAssertAsync(() => Assert.True(File.Exists(resultPath),
                $"The runtime must write {resultPath} when the child ends."), Deadline, cancellationToken: Cancellation);
            await AwaitAssertAsync(() => Assert.True(NoticesFor(journal, accepted.Run.RunId).Count > 0,
                $"The journal must hold a notice that names run {accepted.Run.RunId.Value} and its result file."),
                Deadline, cancellationToken: Cancellation);
            await ExpectTurnsAsync(subscriber, 1);

            // Assert
            var file = await File.ReadAllTextAsync(resultPath, Cancellation);
            Note($"result.md:\n{file}");
            Assert.Contains(ChildOutput, file, StringComparison.Ordinal);
            Assert.Contains("completed", file, StringComparison.OrdinalIgnoreCase);
            var logPath = Host.Services.GetRequiredService<ISessionStorageResolver>().Resolve(sessionId)
                .ForChild(accepted.Run.RunId, accepted.Run.ScopeId).LogPath.Value;
            Assert.Contains(logPath, file, StringComparison.Ordinal);

            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
            var text = notice.UserMessage.Content!;
            Note($"notice: {text}");
            Assert.Contains("child-a", text, StringComparison.Ordinal);
            Assert.Contains("completed", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(resultPath, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ChildOutput, text, StringComparison.Ordinal);
            Assert.Equal(Alice, notice.TurnContext.RequesterSenderId?.Value);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// Spec 2. The next model request has the notice as a user message and no synthetic <c>child_run_result</c> pair.
    /// The model reads the file with a real <c>file_read</c> call, and the text comes back as a real tool result.
    /// Planted bug: the model context keeps the pair projection.
    /// </summary>
    [Fact]
    public async Task Parent_model_reads_the_result_with_a_real_file_read_call()
    {
        // Arrange
        var sessionId = new SessionId("notice/real-file-read");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            // Act
            gate.TrySetResult();
            await ExpectTurnsAsync(subscriber, 1);

            // Assert
            var requests = Clients.Parent.ReceivedMessages;
            var noticeIndex = IndexOf(requests, request => CarriesNoticeFor(request, accepted.Run.RunId));
            Assert.True(noticeIndex >= 0,
                $"No parent model request carries a notice for run {accepted.Run.RunId.Value}. "
                + $"Pair requests: {requests.Count(request => Calls(request).Any(call => call.Name == "child_run_result"))}.");
            Assert.All(requests, request => Assert.DoesNotContain(Calls(request), call => call.Name == "child_run_result"));
            Assert.All(requests, request => Assert.DoesNotContain(Results(request),
                result => result.CallId.StartsWith("child-result-", StringComparison.Ordinal)));

            var resultPath = ResultPath(sessionId, accepted);
            var afterRead = requests.Skip(noticeIndex + 1).First(request => Calls(request).Any(call => call.Name == "file_read"));
            var read = Assert.Single(Calls(afterRead), call => call.Name == "file_read");
            Assert.Equal(resultPath, read.Arguments?["path"]?.ToString());
            var readResult = Assert.Single(Results(afterRead), result => result.CallId == read.CallId);
            Assert.Contains(ChildOutput, readResult.Result?.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// Spec 3. <c>task.md</c> holds the agent, the requester, and the brief when the child first calls the model.
    /// Planted bug: the spawner writes the file after acceptance, or leaves out the requester.
    /// </summary>
    [Fact]
    public async Task Spawn_writes_task_file_before_the_child_runs()
    {
        // Arrange
        var sessionId = new SessionId("notice/task-file");
        await JournalAsync();
        ScriptParent();
        await JoinAsync(sessionId);

        // Act: the child waits at its first model call, so the file must exist before the child starts.
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");

        try
        {
            // Assert
            var taskPath = TaskPath(sessionId, accepted);
            Assert.True(File.Exists(taskPath), $"The spawner must write {taskPath} before the child runs.");
            var file = await File.ReadAllTextAsync(taskPath, Cancellation);
            Note($"task.md:\n{file}");
            Assert.Contains("child-a", file, StringComparison.Ordinal);
            Assert.Contains(Alice, file, StringComparison.Ordinal);
            Assert.Contains($"{Brief} [one]", file, StringComparison.Ordinal);
        }
        finally
        {
            gate.TrySetResult();
        }
    }
}
