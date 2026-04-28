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

    [Theory]
    [InlineData("Well I was going to has You do some Netclaw work for me if")]
    [InlineData("Want to know if I needs To edit that or not")]
    [InlineData("I was just thinking about maybe doing something")]
    [InlineData("You can uses The GH command line utility")]
    public void Rejects_conversational_fragments_from_project_statement_pattern(string input)
    {
        var result = _extractor.Extract(MakeTurnPayload(input), new HashSet<string>());

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Our deployment pipeline uses GitHub Actions for CI/CD and container builds")]
    [InlineData("Netclaw requires Akka.NET 1.5.62 or later for cluster sharding support")]
    public void Accepts_genuine_project_statements(string input)
    {
        var result = _extractor.Extract(MakeTurnPayload(input), new HashSet<string>());

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Turn_complete_without_project_fact_reports_no_project_fact()
    {
        var payload = MakeTurnPayload("This is just a short chat reply.");

        var result = _extractor.ExtractWithDiagnostics(payload, new HashSet<string>());

        Assert.Empty(result.Candidates);
        Assert.Equal(MemoryExtractionDropReason.TurnCompleteNoProjectFact, result.DropReason);
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

    [Fact]
    public void Accepted_project_statement_reports_no_drop_reason()
    {
        var payload = MakeTurnPayload("Our deployment pipeline uses GitHub Actions for CI/CD");

        var result = _extractor.ExtractWithDiagnostics(payload, new HashSet<string>());

        Assert.NotEmpty(result.Candidates);
        Assert.Equal(MemoryExtractionDropReason.None, result.DropReason);
    }
}
