// -----------------------------------------------------------------------
// <copyright file="ShellApprovalEvidenceContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed partial class ShellApprovalEvidenceContractTests
{
    private const string ApprovalMatrixFile = "approval-matrix.json";
    private const string PolicyFixturesFile = "netclaw-policy-fixtures.json";
    private const string PostMergeHarvestFile = "post-1890-approval-harvest.json";
    private const string ApprovalMatrixSha256 =
        "d2a6e64421af337d1c54f1f955934057398176b456e585bfce015ef7ffa24e7d";

    [Fact]
    public void Approval_matrix_matches_the_locked_cross_repository_artifact()
    {
        var bytes = File.ReadAllBytes(EvidencePath(ApprovalMatrixFile));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var matrix = DeserializeMatrix(bytes);

        Assert.Equal(ApprovalMatrixSha256, hash);
        Assert.Equal("Netclaw 0.26.0-beta.3", matrix.SourceRelease);
        Assert.Equal(
            Enumerable.Range(1, 18).Select(number => $"D{number:00}"),
            matrix.Cases.Select(item => item.Id));
        string[] allowedClassifications =
        [
            "CorrectPrompt",
            "IrreduciblyDynamic",
            "NetclawPolicyDefect",
            "ShellSyntaxTreeFactGap"
        ];
        Assert.All(matrix.Cases, item =>
            Assert.Contains(item.Classification, allowedClassifications));
    }

    [Fact]
    public void Policy_fixtures_load_exact_authority_and_trace_fields()
    {
        var matrix = DeserializeMatrix(File.ReadAllBytes(EvidencePath(ApprovalMatrixFile)));
        var fixtures = DeserializeFixtures(File.ReadAllBytes(EvidencePath(PolicyFixturesFile)));
        var commands = matrix.Cases.ToDictionary(item => item.Id, item => item.Command);

        Assert.Equal(1, fixtures.SchemaVersion);
        Assert.Equal("shell_execute", fixtures.FixtureDefaults.ToolName);
        Assert.Equal("Personal", fixtures.FixtureDefaults.Audience);
        Assert.Equal("Approval", fixtures.FixtureDefaults.ApprovalMode);
        Assert.Equal("Available", fixtures.FixtureDefaults.InteractiveApprovalCapability);
        Assert.Equal("Ready", fixtures.FixtureDefaults.PersistentStoreStatus);
        Assert.Equal("fixture-session", fixtures.FixtureDefaults.Session.SessionId);
        Assert.Equal("/work", fixtures.FixtureDefaults.Session.SessionDirectory);
        Assert.Equal("/work", fixtures.FixtureDefaults.ProjectDirectory);
        Assert.Null(fixtures.FixtureDefaults.InheritedWorkingDirectory);
        Assert.Equal(10, fixtures.Cases.Count);

        foreach (var fixture in fixtures.Cases)
        {
            Assert.Equal(commands[fixture.EvidenceId], fixture.Command);
            Assert.Equal("shell_execute", fixtures.FixtureDefaults.ToolName);
            Assert.Equal("Ready", fixtures.FixtureDefaults.PersistentStoreStatus);
            Assert.Equal(
                Enumerable.Range(0, fixture.Candidates.Count),
                fixture.Candidates.Select(candidate => candidate.Id));
            Assert.All(fixture.Available.PersistentGrants, grant =>
            {
                Assert.False(string.IsNullOrWhiteSpace(grant.Shell));
                Assert.NotEmpty(grant.Tokens);
            });

            foreach (var candidate in fixture.Candidates)
            {
                Assert.NotEmpty(candidate.Tokens);
                Assert.Single(fixture.ExpectedTrace, row =>
                    row.CandidateId == candidate.Id
                    && row.Coverage == candidate.ExpectedCoverage);
            }

            var completion = fixture.ExpectedTrace[^1];
            Assert.Equal("Completion", completion.Stage);
            Assert.Null(completion.CandidateId);
            Assert.Equal(fixture.ExpectedFinal.Outcome, completion.Outcome);
            Assert.Equal(fixture.ExpectedFinal.Reason, completion.Reason);
        }
    }

    [Fact]
    public void Exact_symbolic_parser_facts_match_the_policy_fixtures()
    {
        var fixtures = DeserializeFixtures(File.ReadAllBytes(EvidencePath(PolicyFixturesFile)));
        var factCases = fixtures.Cases.Where(item =>
            item.ValueFacts is { Count: > 0 }
            || item.AuthoredPathFacts is { Count: > 0 });

        foreach (var fixture in factCases)
        {
            var environment = CreateEnvironment(fixture.Environment);
            var analysis = new ShellCommandAnalyzer(environment).Analyze(
                fixture.Command,
                fixture.InitialWorkingDirectory);

            Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
            Assert.Equal(fixture.Candidates.Count, analysis.Commands.Count);

            for (var index = 0; index < fixture.Candidates.Count; index++)
            {
                Assert.Equal(
                    fixture.Candidates[index].Tokens,
                    analysis.Commands[index].Clause.Verb.Tokens);
            }

            foreach (var fact in fixture.ValueFacts ?? [])
            {
                var argument = analysis.Commands[fact.CandidateId].Arguments[fact.ArgumentIndex];
                var concatenation = Assert.IsType<ShellValueDomain.Concatenation>(argument.Value);
                Assert.Equal("Concatenation", fact.Domain);
                Assert.Equal(fact.Parts.Count, concatenation.Parts.Count);

                for (var index = 0; index < fact.Parts.Count; index++)
                {
                    AssertValuePart(fact.Parts[index], concatenation.Parts[index]);
                }
            }

            foreach (var fact in fixture.AuthoredPathFacts ?? [])
            {
                var argument = Assert.Single(
                    analysis.Commands[fact.CandidateId].Arguments,
                    item => item.AuthoredPathShape.ToString() == fact.AuthoredPathShape);
                Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
                Assert.Equal("Unknown", fact.EffectiveValue);
                var authored = Assert.IsType<ShellValueDomain.FiniteSet>(argument.AuthoredValue);
                Assert.Equal(fact.AuthoredValues, authored.Values);
                Assert.Equal(fact.AuthoredPathShape, argument.AuthoredPathShape.ToString());
            }
        }
    }

    [Fact]
    public void Authored_path_fixture_keeps_missing_operand_role_strict()
    {
        var fixtures = DeserializeFixtures(File.ReadAllBytes(EvidencePath(PolicyFixturesFile)));
        var fixture = Assert.Single(fixtures.Cases, item => item.AuthoredPathFacts is { Count: > 0 });
        var fact = Assert.Single(fixture.AuthoredPathFacts!);
        var analysis = new ShellCommandAnalyzer(CreateEnvironment(fixture.Environment)).Analyze(
            fixture.Command,
            fixture.InitialWorkingDirectory);
        var argument = Assert.Single(
            analysis.Commands[fact.CandidateId].Arguments,
            item => item.AuthoredPathShape.ToString() == fact.AuthoredPathShape);

        Assert.False(fact.ArgumentIsPath);
        Assert.Equal(fact.ArgumentIsPath, argument.Argument.IsPath);
        Assert.Equal("RequiresApproval", fact.ExpectedPathPolicy);
        Assert.Equal("RequiresApproval", fixture.ExpectedFinal.Outcome);
    }

    [Fact]
    public void Approval_evidence_contains_no_source_identity()
    {
        var evidenceDirectory = Path.GetDirectoryName(EvidencePath(ApprovalMatrixFile))
                                ?? throw new InvalidDataException("Approval evidence has no directory.");
        foreach (var path in Directory.EnumerateFiles(evidenceDirectory, "*.json"))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotMatch(SlackChannelPattern(), text);
            Assert.DoesNotMatch(SlackThreadPattern(), text);
            Assert.DoesNotMatch(EmailPattern(), text);
            Assert.DoesNotMatch(PrivateHomePattern(), text);
            Assert.DoesNotMatch(PrivateWindowsUserPattern(), text);
            Assert.DoesNotMatch(KnownSourceIdentityPattern(), text);
            Assert.DoesNotMatch(AccessTokenPattern(), text);
            Assert.DoesNotMatch(BearerCredentialPattern(), text);
            Assert.DoesNotMatch(CredentialAssignmentPattern(), text);

            foreach (Match match in UriHostPattern().Matches(text))
            {
                Assert.Contains(
                    match.Groups["host"].Value,
                    new[]
                    {
                        "api.github.com",
                        "packages.example.invalid",
                        "service.example.invalid"
                    },
                    StringComparer.OrdinalIgnoreCase);
            }

            foreach (Match match in RemoteRepositoryPattern().Matches(text))
            {
                Assert.Equal("example/project", match.Groups["repository"].Value);
            }
        }
    }

    [Theory]
    [InlineData("ghp_000000000000000000000000000000000000")]
    [InlineData("github_pat_00000000000000000000000000000000")]
    [InlineData("xoxb-0000000000-0000000000-0000000000")]
    [InlineData("Authorization: Bearer example-credential-value")]
    [InlineData("api_key=examplecredentialvalue")]
    public void Pii_audit_detects_common_credential_shapes(string value)
    {
        Assert.True(
            AccessTokenPattern().IsMatch(value)
            || BearerCredentialPattern().IsMatch(value)
            || CredentialAssignmentPattern().IsMatch(value));
    }

    [Fact]
    public void Post_merge_harvest_classifies_every_prompt_in_the_frozen_window()
    {
        var harvest = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath(PostMergeHarvestFile)),
                          ShellApprovalEvidenceJsonContext.Default.PostMergeApprovalHarvest)
                      ?? throw new InvalidDataException($"{PostMergeHarvestFile} has no root object.");

        Assert.Equal(1, harvest.SchemaVersion);
        Assert.Equal("0.26.0", harvest.SourceRuntime.Version);
        Assert.Equal("e35444c", harvest.SourceRuntime.Commit);
        Assert.Equal(112, harvest.SourceRuntime.ShellCallCount);
        Assert.Equal(25, harvest.SourceRuntime.ApprovalPromptCount);
        Assert.Equal(
            Enumerable.Range(1, 25).Select(number => $"P{number:00}"),
            harvest.Cases.Select(item => item.Id));
        Assert.Equal(
            harvest.Cases.Select(item => item.SourcePromptTimeUtc).Order(),
            harvest.Cases.Select(item => item.SourcePromptTimeUtc));
        Assert.All(harvest.Cases, item =>
        {
            Assert.InRange(
                item.SourcePromptTimeUtc,
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowStartUtc),
                DateTimeOffset.Parse(harvest.SourceRuntime.WindowEndUtc));
        });
        Assert.Equal(18, harvest.Cases.Count(item => item.Classification == "ExpectedApproval"));
        Assert.Equal(6, harvest.Cases.Count(item => item.Classification == "AgentAlignmentDebt"));
        Assert.Single(harvest.Cases, item => item.Classification == "NetclawPolicyDebt");
        Assert.DoesNotContain(
            harvest.Cases,
            item => item.Classification == "ShellSyntaxTreeFactGap");
        Assert.All(harvest.Cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.CommandShape));
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
        });
    }

    private static void AssertValuePart(PolicyValuePart expected, ShellValueDomain actual)
    {
        if (expected.Exact is { } exact)
        {
            Assert.Equal(exact, Assert.IsType<ShellValueDomain.Exact>(actual).Value);
            return;
        }

        var range = Assert.IsType<ShellValueDomain.IntegerRange>(actual);
        Assert.NotNull(expected.IntegerRange);
        Assert.Equal(2, expected.IntegerRange.Count);
        Assert.Equal(expected.IntegerRange[0], range.MinimumInclusive);
        Assert.Equal(expected.IntegerRange[1], range.MaximumInclusive);
    }

    private static ShellExecutionEnvironment CreateEnvironment(PolicyFixtureEnvironment environment)
        => environment.Grammar switch
        {
            "Bash" when environment.Platform == "Linux"
                && environment.PathStyle == "Posix"
                && environment.ExecutablePath == "/bin/bash"
                && environment.CommandArguments.SequenceEqual(["-c"])
                => ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            _ => throw new InvalidDataException(
                $"Unsupported fixture environment: {environment.Platform}/{environment.Grammar}.")
        };

    private static ApprovalEvidenceMatrix DeserializeMatrix(byte[] bytes)
        => JsonSerializer.Deserialize(
               bytes,
               ShellApprovalEvidenceJsonContext.Default.ApprovalEvidenceMatrix)
           ?? throw new InvalidDataException($"{ApprovalMatrixFile} has no root object.");

    private static PolicyFixtureCatalog DeserializeFixtures(byte[] bytes)
        => JsonSerializer.Deserialize(
               bytes,
               ShellApprovalEvidenceJsonContext.Default.PolicyFixtureCatalog)
           ?? throw new InvalidDataException($"{PolicyFixturesFile} has no root object.");

    private static string EvidencePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "ApprovalEvidence", fileName);

    [GeneratedRegex(@"\bD[A-Z0-9]{10}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SlackChannelPattern();

    [GeneratedRegex(@"\b\d{10}\.\d{6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SlackThreadPattern();

    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"/home/(?!user/|test/|foo/|dev/|runner/|gh-actions/|ci/)[a-zA-Z0-9_.-]+/", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateHomePattern();

    [GeneratedRegex(@"[A-Za-z]:[\\/]Users[\\/](?!user[\\/]|test[\\/]|foo[\\/]|dev[\\/]|runner[\\/]|ci[\\/])[A-Za-z0-9_.-]+[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateWindowsUserPattern();

    [GeneratedRegex(@"petabridge|stannard|testlab|D0AC6", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownSourceIdentityPattern();

    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/-]{8,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerCredentialPattern();

    [GeneratedRegex(
        @"[""']?(?:token|access[_-]?token|api[_-]?key|client[_-]?secret|password)[""']?\s*[:=]\s*[""']?(?!<)[A-Za-z0-9+/=_-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentPattern();

    [GeneratedRegex(
        @"https?://(?<host>[A-Za-z0-9.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriHostPattern();

    [GeneratedRegex(
        @"(?:--repo\s+|gh\s+api\s+repos/|api\.github\.com/repos/)(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteRepositoryPattern();
}

internal sealed record ApprovalEvidenceMatrix
{
    public required string SourceRelease { get; init; }

    public required string CutoffUtc { get; init; }

    public required Dictionary<string, string> Sanitization { get; init; }

    public required List<ApprovalEvidenceCase> Cases { get; init; }
}

internal sealed record ApprovalEvidenceCase
{
    public required string Id { get; init; }

    public required string Command { get; init; }

    public required string Observed { get; init; }

    public required string Classification { get; init; }

    public required string Owner { get; init; }

    public required string SstExpectation { get; init; }

    public required string NetclawExpectation { get; init; }
}

internal sealed record PolicyFixtureCatalog
{
    public required int SchemaVersion { get; init; }

    public required PolicyFixtureDefaults FixtureDefaults { get; init; }

    public required List<PolicyFixtureCase> Cases { get; init; }
}

internal sealed record PolicyFixtureDefaults
{
    public required string ToolName { get; init; }

    public required string Audience { get; init; }

    public required string ApprovalMode { get; init; }

    public required string InteractiveApprovalCapability { get; init; }

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

    public PolicyParserOptions? ParserOptions { get; init; }

    public required List<PolicyTraceRow> ExpectedTrace { get; init; }

    public required PolicyExpectedFinal ExpectedFinal { get; init; }
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

    public required string ExpectedCoverage { get; init; }
}

internal sealed record PolicyValueFact
{
    public required int CandidateId { get; init; }

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
    public required int CandidateId { get; init; }

    public required bool ArgumentIsPath { get; init; }

    public required string EffectiveValue { get; init; }

    public required List<string> AuthoredValues { get; init; }

    public required string AuthoredPathShape { get; init; }

    public required string ExpectedPathPolicy { get; init; }
}

internal sealed record PolicyShellEffects
{
    public required List<PolicyRedirect> Redirects { get; init; }
}

internal sealed record PolicyRedirect
{
    public required int CandidateId { get; init; }

    public required string Target { get; init; }

    public required string Mode { get; init; }

    public required string ExpectedPathPolicy { get; init; }
}

internal sealed record PolicyParserOptions
{
    public required bool PublishAuthoredSourceFacts { get; init; }
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
}

internal sealed record PostMergeApprovalHarvest
{
    public required int SchemaVersion { get; init; }

    public required PostMergeSourceRuntime SourceRuntime { get; init; }

    public required Dictionary<string, string> Sanitization { get; init; }

    public required List<PostMergeApprovalCase> Cases { get; init; }
}

internal sealed record PostMergeSourceRuntime
{
    public required string Version { get; init; }

    public required string Commit { get; init; }

    public required string WindowStartUtc { get; init; }

    public required string WindowEndUtc { get; init; }

    public required int ShellCallCount { get; init; }

    public required int ApprovalPromptCount { get; init; }
}

internal sealed record PostMergeApprovalCase
{
    public required string Id { get; init; }

    public required DateTimeOffset SourcePromptTimeUtc { get; init; }

    public required string CommandShape { get; init; }

    public required string ObservedResponse { get; init; }

    public required string Classification { get; init; }

    public required string Owner { get; init; }

    public required string Reason { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApprovalEvidenceMatrix))]
[JsonSerializable(typeof(PolicyFixtureCatalog))]
[JsonSerializable(typeof(PostMergeApprovalHarvest))]
internal sealed partial class ShellApprovalEvidenceJsonContext : JsonSerializerContext;
