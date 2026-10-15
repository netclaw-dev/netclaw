// -----------------------------------------------------------------------
// <copyright file="DeployedV2JournalReplayTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Spec 14 (E5). The deployed daemon wrote child results in the form of this stack: <c>cre-v2</c> rows with
/// <c>ResultPrepared</c>, <c>tbs-v1</c> and <c>tr-v1</c> rows with a child continuation, and <c>ss-v2</c> snapshots with
/// the lifecycle states prepared, consumed, and closed. S2 deletes those writers. The bytes in
/// <c>Fixtures/CoordinationV2</c> come from the writers at revision c4ed4dcc6a2165d92d074dfd6cf603d2873bca13. Nothing
/// else can make them after S2.
/// <para>
/// A session recovers over those rows through the production reader. Every result reaches the model exactly once. A
/// result that the parent already read stays reported: the legacy tool pair sits once in the history and no notice
/// follows. A result that the parent never read becomes one notice and no pair.
/// </para>
/// </summary>
public sealed class DeployedV2JournalReplayTests(ITestOutputHelper output)
    : RecordedJournalTestBase(output, "CoordinationV2", "v2-writer-records.json", "c4ed4dcc6a2165d92d074dfd6cf603d2873bca13")
{
    /// <summary>
    /// A result that the parent consumed before the upgrade stays reported. The recovery makes no model call and no
    /// notice, and the history holds the legacy pair of the run once.
    /// Planted bug: the reader maps <c>ResultPrepared</c> to the terminal state and ignores the child continuation, so the
    /// result is delivered a second time. A second planted bug drops the pair, and the history loses the tool pair that a
    /// later assistant turn answers.
    /// </summary>
    [Theory]
    [InlineData("delivered-text")]
    [InlineData("delivered-tools")]
    [InlineData("snapshot-consumed")]
    [InlineData("snapshot-closed")]
    public async Task A_delivered_result_stays_reported_with_its_legacy_pair_once_and_no_notice(string scenario)
    {
        // Arrange
        var recorded = await SeedAsync(scenario);
        var subscriber = await JoinAsync(recorded.SessionId);

        // Act: the recovery alone must not call the model. A new message then shows the history.
        var callsAfterRecovery = Clients.Parent.CallCount;
        await SendAsync(recorded.SessionId, "Continue.", Operator(Alice));
        await ExpectTurnsAsync(subscriber, 1);

        // Assert
        Assert.Equal(0, callsAfterRecovery);
        var request = Clients.Parent.ReceivedMessages.Last();
        var pair = Assert.Single(Calls(request), call => call.Name == "child_run_result"
                                                         && call.Arguments?["run_id"]?.ToString() == recorded.RunId);
        Assert.Single(Results(request), result => result.CallId == pair.CallId);
        Assert.False(CarriesNoticeFor(request, new SubAgentRunId(recorded.RunId)),
            "A result that the parent consumed must not return as a notice.");
        if (scenario == "delivered-tools")
        {
            var read = Assert.Single(Calls(request), call => call.CallId == "wake-read");
            Assert.Single(Results(request), result => result.CallId == read.CallId);
        }
    }

    /// <summary>
    /// A result that the parent never read becomes one notice at the recovery and no legacy pair. The recovery writes
    /// <c>result.md</c> from the recorded terminal result, and a later turn does not admit the notice again.
    /// Planted bug: the recovery delivers the legacy pair, or both the pair and a notice.
    /// </summary>
    [Theory]
    [InlineData("undelivered")]
    [InlineData("snapshot-prepared")]
    public async Task An_undelivered_result_gets_exactly_one_notice_and_no_legacy_pair(string scenario)
    {
        // Arrange
        ScriptParent();
        var recorded = await SeedAsync(scenario);
        var runId = new SubAgentRunId(recorded.RunId);

        // Act
        var subscriber = await JoinAsync(recorded.SessionId);
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages, request => CarriesNoticeFor(request, runId)) >= 0,
            $"No model request carries a notice for run {recorded.RunId}. Requests with a legacy pair for the run: "
            + $"{Clients.Parent.ReceivedMessages.Count(request => Calls(request).Any(call => call.Name == "child_run_result"))}."),
            Deadline, cancellationToken: Cancellation);
        await ExpectTurnsAsync(subscriber, 1);
        await SendAsync(recorded.SessionId, "Continue.", Operator(Alice));
        await ExpectTurnsAsync(subscriber, 1);

        // Assert
        var resultPath = Path.Combine(RunFolder(recorded.SessionId, runId, new SubAgentScopeId("capture-scope")), "result.md");
        Assert.True(File.Exists(resultPath), $"The recovery must write {resultPath}.");
        Assert.Contains(ChildOutput, await File.ReadAllTextAsync(resultPath, Cancellation), StringComparison.Ordinal);
        Assert.All(Clients.Parent.ReceivedMessages, request => Assert.DoesNotContain(Calls(request),
            call => call.Name == "child_run_result"));
        var last = Clients.Parent.ReceivedMessages.Last();
        Assert.Single(last, message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                                       && message.Text.Contains(recorded.RunId, StringComparison.Ordinal)
                                       && message.Text.Contains("result.md", StringComparison.Ordinal));
    }

    private async Task<RecordedScenario> SeedAsync(string scenario)
    {
        var corpus = JsonSerializer.Deserialize<ScenarioCorpus>(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "CoordinationV2", "v2-writer-records.json"), Cancellation))!;
        var recorded = corpus.Scenarios.Single(item => item.Scenario == scenario);
        var sessionId = new SessionId(recorded.SessionId);
        var persistenceId = $"session-{sessionId.Value}";
        if (scenario.StartsWith("snapshot-", StringComparison.Ordinal))
            await SeedSnapshotAsync(persistenceId, $"{scenario}-seeder", recorded.Rows.Single());
        else
            await SeedJournalAsync(persistenceId, $"{scenario}-seeder", recorded.Rows);
        return new RecordedScenario(sessionId, recorded.RunId);
    }

    private sealed record RecordedScenario(SessionId SessionId, string RunId);

    private sealed class ScenarioCorpus
    {
        [JsonPropertyName("scenarios")] public ScenarioRecord[] Scenarios { get; init; } = [];
    }

    private sealed class ScenarioRecord
    {
        [JsonPropertyName("scenario")] public string Scenario { get; init; } = string.Empty;
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
        [JsonPropertyName("runId")] public string RunId { get; init; } = string.Empty;
        [JsonPropertyName("rows")] public string[] Rows { get; init; } = [];
    }
}
