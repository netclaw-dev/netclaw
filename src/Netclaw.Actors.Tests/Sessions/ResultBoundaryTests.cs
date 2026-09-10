// -----------------------------------------------------------------------
// <copyright file="ResultBoundaryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ResultBoundaryTests
{
    [Fact]
    public void Receipt_cases_retain_payload_ownership_and_reject_wrong_categories()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "receipt-file.txt"));
        var activity = new List<ToolFileActivity> { new(path, ToolFileActivityKind.Read) };
        var success = new ToolInvocationReceipt.Succeeded(activity, null);
        activity.Clear();
        var updated = WorkingContextUpdater.UpdateFromToolReceipt(WorkingContext.Empty, success);
        Assert.Contains(path, updated.RecentFiles);
        Assert.Throws<ArgumentException>(() => new ToolInvocationReceipt.Succeeded([], "relative/project"));
        Assert.Same(updated, WorkingContextUpdater.UpdateFromToolReceipt(updated,
            new ToolInvocationReceipt.Correction(ToolRemediationCode.SetWorkingDirectory)));
    }

    [Theory]
    [InlineData(false, "main", "abc123", "origin/main", 2, 1)]
    [InlineData(false, "main", null, null, 0, 0)]
    [InlineData(false, null, null, null, 0, 0)]
    [InlineData(true, null, "abc123", null, 0, 0)]
    public void Git_old_shape_round_trip_retains_head_state_and_context_text(
        bool detached, string? branch, string? head, string? upstream, int ahead, int behind)
    {
        var oldShape = new JsonObject
        {
            ["Worktree"] = "/repo", ["CommonDirectory"] = "/repo/.git", ["Branch"] = branch,
            ["Detached"] = detached, ["Head"] = head, ["Upstream"] = upstream, ["Ahead"] = ahead,
            ["Behind"] = behind, ["Staged"] = 1, ["Modified"] = 2, ["Untracked"] = 3,
            ["ChangedFiles"] = new JsonArray("a.txt")
        };
        var loaded = oldShape.Deserialize<GitWorkingContextSnapshot>()!;
        var mapped = loaded.HeadState.ApplyTo(loaded);
        Assert.True(JsonNode.DeepEquals(oldShape, JsonSerializer.SerializeToNode(mapped)));
        var block = new WorkingContextSnapshot
        {
            WorkingContext = WorkingContext.Empty,
            Git = new GitWorkingContextInspection.Available(mapped)
        }.ToContextBlock();
        Assert.Contains($"branch: {(detached ? "(detached)" : branch)}\n  head: {head ?? "(unborn)"}", block);
        Assert.Equal(upstream is not null, block.Contains("upstream:", StringComparison.Ordinal));
        if (detached)
            Assert.IsType<GitHeadState.DetachedHead>(mapped.HeadState);
        else
            Assert.IsType<GitHeadState.AttachedHead>(mapped.HeadState);
    }

    [Theory]
    [InlineData("# branch.oid abc123\n# branch.head (detached)\n# branch.upstream origin/main")]
    [InlineData("# branch.oid (initial)\n# branch.head (detached)")]
    [InlineData("# branch.oid abc123\n# branch.head main\n# branch.ab +2 -1")]
    public void Git_parser_rejects_contradictory_head_metadata(string status)
        => Assert.Throws<FormatException>(() => GitWorkingContextInspector.ParseStatus("/repo", "/repo/.git", status));

    [Theory]
    [InlineData("completed")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Child_enrichment_preserves_public_shape_and_completion(string outcome)
    {
        ChildRunCompletion completion = outcome switch
        {
            "completed" => new ChildRunCompletion.Completed(WorkingContextDelta.Empty),
            "partial" => new ChildRunCompletion.Partial(SubAgentOutcomeReason.ToolIterationBudgetExhausted, WorkingContextDelta.Empty),
            "failed" => new ChildRunCompletion.Failed(SubAgentOutcomeReason.LlmCallFailed),
            "cancelled" => new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent),
            _ => throw new ArgumentException("Unexpected test outcome.", nameof(outcome))
        };
        var actorResponse = new SubAgentResult { Completion = completion, Output = "result", AgentName = new AgentName("helper") };
        var storage = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "child-result-fixture"))));
        var locations = new EnrichedChildRunResult.RunLocations(storage.LogPath, storage.ArtifactDirectory);
        Assert.Throws<ArgumentNullException>(() => new EnrichedChildRunResult.RunLocations(default, storage.ArtifactDirectory));
        EnrichedChildRunResult enriched = completion.Success
            ? new EnrichedChildRunResult.SuccessfulRun(actorResponse, locations)
            : new EnrichedChildRunResult.OtherRun(actorResponse);
        var protocol = enriched.ToProtocolResult();
        Assert.Same(completion, protocol.Completion);
        Assert.Null(actorResponse.LogPath);
        Assert.Null(actorResponse.ArtifactDirectory);
        var expected = actorResponse with
        {
            LogPath = completion.Success ? storage.LogPath.Value : null,
            ArtifactDirectory = completion.Success ? storage.ArtifactDirectory.Value : null
        };
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(protocol)));
        if (completion.Success)
            Assert.Throws<ArgumentException>(() => new EnrichedChildRunResult.OtherRun(actorResponse));
        else
            Assert.Throws<ArgumentException>(() => new EnrichedChildRunResult.SuccessfulRun(actorResponse, locations));
    }
}
