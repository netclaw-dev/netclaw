// -----------------------------------------------------------------------
// <copyright file="MemoryRulesFirstExtractorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryRulesFirstExtractorTests
{
    private readonly MemoryRulesFirstExtractor _extractor = new(new MemoryPolicyEvaluator());

    private static MemoryCheckpointPayload MakeTurnPayload(string userContent) => new(
        SessionId: "D0AC6CKBK5K/1774370274.953879",
        TriggerType: CheckpointTriggerType.TurnComplete.ToWireValue(),
        Source: "session",
        Content: userContent,
        UserContent: userContent,
        AssistantContent: null,
        IsExplicitRequest: false,
        HasVerifiedToolFinding: false,
        IsCompactionBoundary: false,
        HasAcceptedSubAgentFinding: false,
        Sensitivity: "normal",
        RecallMode: "auto",
        Confidence: 0.88);

    private static MemoryCheckpointPayload MakeCompactionPayload(string summary) => new(
        SessionId: "D0AC6CKBK5K/1774370274.953879",
        TriggerType: "compaction-boundary",
        Source: "compaction",
        Content: summary,
        UserContent: null,
        AssistantContent: summary,
        IsExplicitRequest: false,
        HasVerifiedToolFinding: false,
        IsCompactionBoundary: true,
        HasAcceptedSubAgentFinding: false,
        Sensitivity: "normal",
        RecallMode: MemoryRecallMode.Auto.ToWireValue(),
        Confidence: 0.8,
        Kind: MemoryKind.Document.ToWireValue(),
        Title: "compaction-boundary",
        UpdateSemantics: "append-document");

    [Fact]
    public void Compaction_boundary_is_retained_but_not_auto_recallable()
    {
        // Regression guard for issue 1224: compaction summaries are whole-session
        // blobs that pollute automatic recall. They must be retained but kept out
        // of the auto-recall pool (which fetches only Auto/Searchable). Manual does
        // that; the summary compaction relies on lives in the session record.
        var summary = "## 1. Primary Request and Intent\n"
                      + "The user wanted to deploy the agent fleet across the test lab. "
                      + "Decisions: use Kata containers for isolation; PostgreSQL for persistence.";

        var candidate = Assert.Single(_extractor.Extract(MakeCompactionPayload(summary), new HashSet<string>()));

        Assert.Equal(MemoryClass.Evidence, candidate.MemoryClass);
        Assert.Equal(MemoryRecallMode.Manual, candidate.RecallMode);
        Assert.NotEqual(MemoryRecallMode.Auto, candidate.RecallMode);
        Assert.NotEqual(MemoryRecallMode.Searchable, candidate.RecallMode);
    }

    [Theory]
    [InlineData("This is just a short chat reply.")]
    [InlineData("Our deployment pipeline uses GitHub Actions for CI/CD and container builds")]
    [InlineData("Netclaw requires Akka.NET 1.5.62 or later for cluster sharding support")]
    public void Retired_turn_complete_checkpoint_drains_to_zero_candidates(string input)
    {
        // Backward-compatibility guard for issue 666. No code enqueues a turn-complete
        // checkpoint now, but an upgraded installation can still hold turn-complete rows
        // in the SQLite checkpoint queue. The worker must drain such a row, and must not
        // write the queued turn transcript to memory through the general path.
        var result = _extractor.ExtractWithDiagnostics(MakeTurnPayload(input), new HashSet<string>());

        Assert.Empty(result.Candidates);
        Assert.Equal(MemoryExtractionDropReason.TurnCompleteRetired, result.DropReason);
    }

    [Fact]
    public void Empty_content_reports_empty_content_drop_reason()
    {
        var payload = MakeTurnPayload(string.Empty);

        var result = _extractor.ExtractWithDiagnostics(payload, new HashSet<string>());

        Assert.Empty(result.Candidates);
        Assert.Equal(MemoryExtractionDropReason.EmptyContent, result.DropReason);
    }

    [Fact]
    public void Ephemeral_content_reports_ephemeral_drop_reason()
    {
        var payload = MakeTurnPayload("thanks");

        var result = _extractor.ExtractWithDiagnostics(payload, new HashSet<string>());

        Assert.Empty(result.Candidates);
        Assert.Equal(MemoryExtractionDropReason.EphemeralContent, result.DropReason);
    }

}
