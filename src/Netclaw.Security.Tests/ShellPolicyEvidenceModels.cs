// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvidenceModels.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Security.Tests;

internal sealed record PolicyFixtureCatalog
{
    public required int SchemaVersion { get; init; }

    public required PolicyFixtureDefaults FixtureDefaults { get; init; }

    public required List<PolicyFixtureCase> Cases { get; init; }

    public required List<PolicyLiveRegressionCase> LiveRegressionCases { get; init; }

    public required List<PolicyAdversarialCase> AdversarialCases { get; init; }
}

internal sealed record PolicyLiveRegressionCase
{
    public required string SourceEvidenceFile { get; init; }

    public required string SourceEvidenceId { get; init; }

    public required string Classification { get; init; }

    public required string TargetOutcome { get; init; }

    public required PolicyAdversarialCase PolicyCase { get; init; }
}

internal sealed record PolicyFixtureDefaults
{
    public required string ToolName { get; init; }

    public required string Audience { get; init; }

    public required string ApprovalMode { get; init; }

    public required string InteractiveApprovalCapability { get; init; }

    public required string ClockUtc { get; init; }

    public required PolicyFixtureSession Session { get; init; }

    public required string ProjectDirectory { get; init; }

    public string? InheritedWorkingDirectory { get; init; }

    public required string PersistentStoreStatus { get; init; }
}

internal sealed record PolicyFixtureSession
{
    public required string SessionId { get; init; }

    public required string SessionDirectory { get; init; }
}

internal sealed record PolicyFixtureCase
{
    public required string EvidenceId { get; init; }

    public required string Command { get; init; }

    public required PolicyFixtureEnvironment Environment { get; init; }

    public required string InitialWorkingDirectory { get; init; }

    public required PolicyFixtureAuthority Available { get; init; }

    public required List<PolicyFixtureCandidate> Candidates { get; init; }

    public List<PolicyValueFact>? ValueFacts { get; init; }

    public List<PolicyAuthoredPathFact>? AuthoredPathFacts { get; init; }

    public PolicyShellEffects? ShellEffects { get; init; }

    public required List<PolicyTraceRow> ExpectedTrace { get; init; }

    public required PolicyExpectedFinal ExpectedFinal { get; init; }
}

internal sealed record PolicyAdversarialCase
{
    public required string Id { get; init; }

    public required string Category { get; init; }

    public required string Command { get; init; }

    public required PolicyFixtureEnvironment Environment { get; init; }

    public required string InitialWorkingDirectory { get; init; }

    public required string ProjectDirectory { get; init; }

    public required string SessionDirectory { get; init; }

    public required PolicyFixtureAuthority Available { get; init; }

    public bool UseBundledSafeCatalog { get; init; }

    public required PolicyAdversarialExpected Expected { get; init; }
}

internal sealed record PolicyAdversarialExpected
{
    public required string Outcome { get; init; }

    public string? DenyReason { get; init; }

    public string? AgentCorrection { get; init; }

    public List<string>? ApprovalCandidates { get; init; }

    public bool? IsMessy { get; init; }

    public List<string>? OptionKeys { get; init; }

    public required int ActorCheckCount { get; init; }
}

internal sealed record PolicyFixtureEnvironment
{
    public required string Platform { get; init; }

    public required string ExecutablePath { get; init; }

    public required List<string> CommandArguments { get; init; }

    public required string Grammar { get; init; }

    public required string PathStyle { get; init; }

    public string? PowerShellDialect { get; init; }
}

internal sealed record PolicyFixtureAuthority
{
    public required List<string> OneTimeApprovalKeys { get; init; }

    public required List<PolicyGrant> SessionGrants { get; init; }

    public required List<PolicyGrant> PersistentGrants { get; init; }

    public required List<PolicySafePhrase> SafePhrases { get; init; }
}

internal sealed record PolicyGrant
{
    public required string Kind { get; init; }

    public required string Shell { get; init; }

    public required string Match { get; init; }

    public required List<string> Tokens { get; init; }

    public string? Directory { get; init; }
}

internal sealed record PolicySafePhrase
{
    public required List<string> Tokens { get; init; }

    public required string Proof { get; init; }
}

internal sealed record PolicyFixtureCandidate
{
    public required int Id { get; init; }

    public required List<string> Tokens { get; init; }

    public string? RealDirectory { get; init; }

    public string? IntentDirectory { get; init; }

    public string? Role { get; init; }

    public List<int>? PrerequisiteIds { get; init; }

    public required string ExpectedCoverage { get; init; }
}

internal sealed record PolicyValueFact
{
    public required int CommandIndex { get; init; }

    public required List<string> VerbTokens { get; init; }

    public required int ArgumentIndex { get; init; }

    public required string Domain { get; init; }

    public required List<PolicyValuePart> Parts { get; init; }
}

internal sealed record PolicyValuePart
{
    public string? Exact { get; init; }

    public List<long>? IntegerRange { get; init; }
}

internal sealed record PolicyAuthoredPathFact
{
    public required int CommandIndex { get; init; }

    public required List<string> VerbTokens { get; init; }

    public required bool ArgumentIsPath { get; init; }

    public required string EffectiveValue { get; init; }

    public required List<string> AuthoredValues { get; init; }

    public required List<string> AuthoredFileSystemValues { get; init; }

    public required string AuthoredPathShape { get; init; }

    public required string ExpectedPathPolicy { get; init; }
}

internal sealed record PolicyShellEffects
{
    public required List<PolicyRedirect> Redirects { get; init; }

    public List<PolicyWorkingDirectoryEffect>? WorkingDirectoryEffects { get; init; }
}

internal sealed record PolicyWorkingDirectoryEffect
{
    public required int CommandIndex { get; init; }

    public required string Kind { get; init; }

    public required List<string> Targets { get; init; }
}

internal sealed record PolicyRedirect
{
    public required int CommandIndex { get; init; }

    public required string Target { get; init; }

    public required string Mode { get; init; }

    public required string ExpectedPathPolicy { get; init; }
}

internal sealed record PolicyTraceRow
{
    public required string Stage { get; init; }

    public int? CandidateId { get; init; }

    public string? ExecutableBasename { get; init; }

    public required string Outcome { get; init; }

    public required string Reason { get; init; }

    public string? Coverage { get; init; }

    public string? ScopeRelation { get; init; }

    public string? GrantTimestamp { get; init; }
}

internal sealed record PolicyExpectedFinal
{
    public required string Outcome { get; init; }

    public required string Reason { get; init; }

    public List<string>? ApprovalCandidates { get; init; }

    public bool? IsMessy { get; init; }

    public string? AgentCorrection { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PolicyFixtureCatalog))]
internal sealed partial class ShellPolicyFixtureJsonContext : JsonSerializerContext;
