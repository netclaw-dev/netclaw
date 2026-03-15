using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class SubAgentFindingReviewTests
{
    [Fact]
    public void Review_accepts_durable_reusable_matching_findings()
    {
        var finding = CreateFinding();

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal(SubAgentFindingReviewDecision.Accepted, result.Decision);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Review_defers_missing_durability()
    {
        var finding = CreateFinding() with { Durability = (SubAgentFindingDurability)999 };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal(SubAgentFindingReviewDecision.Deferred, result.Decision);
        Assert.Equal("missing durability", result.Reason);
    }

    [Fact]
    public void Review_defers_non_reusable_findings()
    {
        var finding = CreateFinding() with { Reusability = SubAgentFindingReusability.TaskLocal };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal(SubAgentFindingReviewDecision.Deferred, result.Decision);
        Assert.Equal("insufficient reusability", result.Reason);
    }

    [Fact]
    public void Review_rejects_raw_work_log_content()
    {
        var finding = CreateFinding() with
        {
            Shape = SubAgentFindingShape.Worklog,
            Content = "Step 1: I called file_read. Step 2: I inspected stdout: done."
        };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal(SubAgentFindingReviewDecision.Rejected, result.Decision);
        Assert.Equal("unsupported shape", result.Reason);
    }

    [Fact]
    public void Review_rejects_policy_denied_secret_auto_recall()
    {
        var finding = CreateFinding() with
        {
            Sensitivity = SubAgentFindingSensitivity.Secret,
            RecallMode = SubAgentFindingRecallMode.Auto
        };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal(SubAgentFindingReviewDecision.Rejected, result.Decision);
        Assert.Equal("secret cannot auto-recall", result.Reason);
    }

    [Fact]
    public void Review_defers_domain_mismatch()
    {
        var finding = CreateFinding() with { Domain = "project:other" };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal(SubAgentFindingReviewDecision.Deferred, result.Decision);
        Assert.Contains("domain mismatch", result.Reason);
    }

    private static SubAgentFindingCandidate CreateFinding()
        => new()
        {
            Title = "subagent:research-assistant",
            Content = "Netclaw uses SQLite journal persistence for session durability in the current deployment.",
            Kind = "record",
            Domain = "project:project-a",
            Sensitivity = SubAgentFindingSensitivity.Normal,
            RecallMode = SubAgentFindingRecallMode.Searchable,
            UpdateSemantics = "append-document",
            Confidence = 0.8,
            Shape = SubAgentFindingShape.Conclusion,
            Durability = SubAgentFindingDurability.Durable,
            Reusability = SubAgentFindingReusability.Reusable,
            Evidence = ["docs/architecture.md"]
        };
}
