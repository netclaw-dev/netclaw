// -----------------------------------------------------------------------
// <copyright file="FreshSessionApprovalEvidenceModels.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Security.Tests;

internal sealed record FreshSessionApprovalHarvest
{
    public required int SchemaVersion { get; init; }

    public required FreshSessionSourceRuntime SourceRuntime { get; init; }

    public required Dictionary<string, string> Sanitization { get; init; }

    public required List<FreshSessionSummary> Sessions { get; init; }

    public required List<FreshPromptClassification> PromptClassifications { get; init; }

    public required List<FreshRepresentativeCase> RepresentativeCases { get; init; }
}

internal sealed record FreshSessionSourceRuntime
{
    public required string Version { get; init; }

    public required string Commit { get; init; }

    public required string BinarySha256 { get; init; }

    public required string WindowStartHourUtc { get; init; }

    public required string WindowEndHourUtc { get; init; }

    public required int ParentSessionCount { get; init; }

    public required int ChildLogCount { get; init; }

    public required int ShellCallCount { get; init; }

    public required int ApprovalPromptCount { get; init; }
}

internal sealed record FreshSessionSummary
{
    public required string Id { get; init; }

    public required string StartHourUtc { get; init; }

    public required int ParentShellCalls { get; init; }

    public required int ChildShellCalls { get; init; }

    public required int ChildLogCount { get; init; }

    public required int ApprovalPromptCount { get; init; }

    public required int ExpectedApprovalCount { get; init; }

    public required int AgentAlignmentDebtCount { get; init; }

    public required int ShellSyntaxTreeFactGapCount { get; init; }

    public required int NetclawPolicyDefectCount { get; init; }
}

internal sealed record FreshPromptClassification
{
    public required string Id { get; init; }

    public required string Session { get; init; }

    public required string Source { get; init; }

    public required string Classification { get; init; }

    public required string Owner { get; init; }

    public required string ReasonCode { get; init; }
}

internal sealed record FreshRepresentativeCase
{
    public required string Id { get; init; }

    public required string Source { get; init; }

    public required bool IncludedInBaseline { get; init; }

    public required string CommandShape { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string Classification { get; init; }

    public required string Owner { get; init; }

    public required string ObservedOutcome { get; init; }

    public required string TargetOutcome { get; init; }

    public required string Reason { get; init; }

    public List<FreshFileSystemFact>? FileSystemFacts { get; init; }
}

internal sealed record FreshFileSystemFact
{
    public required string Kind { get; init; }

    public required string Path { get; init; }

    public required string Target { get; init; }
}

internal sealed record FreshSessionEvalBaseline
{
    public required int SchemaVersion { get; init; }

    public required FreshSessionEvalRuntime Runtime { get; init; }

    public required Dictionary<string, string> Sanitization { get; init; }

    public FreshSessionEvalSummary? Summary { get; init; }

    public required List<FreshSessionEvalCase> Cases { get; init; }
}

internal sealed record FreshSessionEvalRuntime
{
    public required string Version { get; init; }

    public string? Commit { get; init; }

    public string? BaseCommit { get; init; }

    public required string ImageSha256 { get; init; }

    public required string Model { get; init; }

    public required string ProviderType { get; init; }

    public required int RunsPerCase { get; init; }

    public required bool InteractiveApprovalAvailable { get; init; }

    public required string SourceState { get; init; }

    public required string ApprovalEventDefinition { get; init; }

    public string? ExpectedBoundaryDefinition { get; init; }
}

internal sealed record FreshSessionEvalSummary
{
    public required int BehaviorPassCount { get; init; }

    public required int BehaviorRunCount { get; init; }

    public required int ApprovalPromptEquivalentCount { get; init; }

    public required int TrustZoneHardDenyCount { get; init; }

    public required int BaselineApprovalPromptEquivalentCount { get; init; }

    public required int BaselineTrustZoneHardDenyCount { get; init; }

    public required string Interpretation { get; init; }
}

internal sealed record FreshSessionEvalCase
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Classification { get; init; }

    public required string Owner { get; init; }

    public required int Runs { get; init; }

    public required int BehaviorPassCount { get; init; }

    public required int TaskCompletionCount { get; init; }

    public required int LlmRequestCount { get; init; }

    public required int ApprovalPromptEquivalentCount { get; init; }

    public required int TrustZoneHardDenyCount { get; init; }

    public required int SuccessfulShellCallCount { get; init; }

    public required int ChildAttemptCount { get; init; }

    public required int ChildFailureCount { get; init; }

    public required int ChildProjectDeclarationCount { get; init; }

    public required Dictionary<string, int> ParentToolCalls { get; init; }

    public required Dictionary<string, int> ChildToolCalls { get; init; }

    public required string RetainedBoundary { get; init; }

    public string? ExpectedIntervention { get; init; }

    public string? BaselineComparison { get; init; }
}
