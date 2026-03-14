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

        Assert.Equal("accepted", result.Decision);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Review_defers_missing_durability()
    {
        var finding = CreateFinding() with { Durability = "" };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal("deferred", result.Decision);
        Assert.Equal("missing durability", result.Reason);
    }

    [Fact]
    public void Review_defers_non_reusable_findings()
    {
        var finding = CreateFinding() with { Reusability = "task-local" };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal("deferred", result.Decision);
        Assert.Equal("insufficient reusability", result.Reason);
    }

    [Fact]
    public void Review_rejects_raw_work_log_content()
    {
        var finding = CreateFinding() with
        {
            Shape = "worklog",
            Content = "Step 1: I called file_read. Step 2: I inspected stdout: done."
        };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal("rejected", result.Decision);
        Assert.Equal("unsupported shape", result.Reason);
    }

    [Fact]
    public void Review_rejects_policy_denied_secret_auto_recall()
    {
        var finding = CreateFinding() with
        {
            Sensitivity = "secret",
            RecallMode = "auto"
        };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal("rejected", result.Decision);
        Assert.Equal("secret cannot auto-recall", result.Reason);
    }

    [Fact]
    public void Review_defers_domain_mismatch()
    {
        var finding = CreateFinding() with { Domain = "project:other" };

        var result = LlmSessionActor.ReviewSubAgentFinding(finding, "project-a/thread-1");

        Assert.Equal("deferred", result.Decision);
        Assert.Contains("domain mismatch", result.Reason);
    }

    private static SubAgentFindingCandidate CreateFinding()
        => new()
        {
            Title = "subagent:research-assistant",
            Content = "Netclaw uses SQLite journal persistence for session durability in the current deployment.",
            Kind = "record",
            Domain = "project:project-a",
            Sensitivity = "normal",
            RecallMode = "searchable",
            UpdateSemantics = "append-document",
            Confidence = 0.8,
            Shape = "conclusion",
            Durability = "durable",
            Reusability = "reusable",
            Evidence = ["docs/architecture.md"]
        };
}
