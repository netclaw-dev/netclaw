// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvidenceFixtureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Tests;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellPolicyEvidenceFixtureTests(ShellApprovalMatrixFixture fixture) :
    IClassFixture<ShellApprovalMatrixFixture>
{
    [Fact]
    public async Task Policy_fixtures_execute_through_the_coordinator()
    {
        var catalog = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath()),
                          ShellPolicyFixtureJsonContext.Default.PolicyFixtureCatalog)
                      ?? throw new InvalidDataException("The policy fixture catalog has no root object.");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse(
            catalog.FixtureDefaults.ClockUtc,
            CultureInfo.InvariantCulture));
        var expectedRows = new List<string>();
        var actualRows = new List<string>();
        var expectedOutcomes = new List<ToolAuthorizationOutcome>();
        var actualOutcomes = new List<ToolAuthorizationOutcome>();

        foreach (var policyCase in catalog.Cases)
        {
            var invocation = CreateInvocation(catalog.FixtureDefaults, policyCase);
            AssertProjectedCandidates(policyCase, invocation);
            var approvals = CreateApprovals(policyCase);
            await using var harness = await ShellApprovalHarness.CreateAsync(
                policyCase.EvidenceId,
                invocation,
                approvals,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                timeProvider,
                new ShellApprovalHarnessScope(
                    catalog.FixtureDefaults.ProjectDirectory,
                    catalog.FixtureDefaults.Session.SessionDirectory,
                    catalog.FixtureDefaults.Session.SessionId,
                    policyCase.Available.OneTimeApprovalKeys),
                CreateSafeVerbs(policyCase.Available, invocation.CreateEnvironment()));
            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{policyCase.EvidenceId}: outcome={decision.Outcome}; "
                + $"candidates={string.Join(", ", decision.ApprovalContext?.CandidateVerbs ?? [])}; "
                + $"messy={decision.ApprovalContext?.IsMessy}\n"
                + string.Join(Environment.NewLine, decision.ShellPolicyTrace.Rows.Select(FormatActualTraceRow)));

            expectedOutcomes.Add(ParseOutcome(policyCase.ExpectedFinal.Outcome));
            actualOutcomes.Add(decision.Outcome);
            Assert.Equal(
                policyCase.ExpectedFinal.ApprovalCandidates,
                decision.ApprovalContext?.CandidateVerbs);
            Assert.Equal(policyCase.ExpectedFinal.IsMessy, decision.ApprovalContext?.IsMessy);
            Assert.Equal(
                policyCase.ExpectedFinal.AgentCorrection,
                decision.ApprovalContext?.AgentCorrection?.GetType().Name);
            expectedRows.AddRange(policyCase.ExpectedTrace.Select(row =>
                $"{policyCase.EvidenceId}|{FormatExpectedTraceRow(row)}"));
            actualRows.AddRange(decision.ShellPolicyTrace.Rows.Select(row =>
                $"{policyCase.EvidenceId}|{FormatActualTraceRow(row)}"));
        }

        Assert.Equal(expectedOutcomes, actualOutcomes);
        Assert.Equal(expectedRows, actualRows);
    }

    [Fact]
    public async Task Adversarial_policy_fixtures_fail_closed_through_the_coordinator()
    {
        var catalog = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath()),
                          ShellPolicyFixtureJsonContext.Default.PolicyFixtureCatalog)
                      ?? throw new InvalidDataException("The policy fixture catalog has no root object.");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse(
            catalog.FixtureDefaults.ClockUtc,
            CultureInfo.InvariantCulture));

        foreach (var policyCase in catalog.AdversarialCases)
        {
            await AssertPolicyCaseAsync(catalog, timeProvider, policyCase);
        }
    }

    public static TheoryData<string> LiveRegressionCaseIds => new(
        Enumerable.Range(1, 32).Select(number => $"L{number:00}"));

    [Theory]
    [MemberData(nameof(LiveRegressionCaseIds))]
    public async Task Live_regression_fixtures_pin_current_policy_outcomes(string caseId)
    {
        var catalog = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath()),
                          ShellPolicyFixtureJsonContext.Default.PolicyFixtureCatalog)
                      ?? throw new InvalidDataException("The policy fixture catalog has no root object.");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse(
            catalog.FixtureDefaults.ClockUtc,
            CultureInfo.InvariantCulture));

        var liveCase = Assert.Single(
            catalog.LiveRegressionCases,
            item => item.PolicyCase.Id == caseId);
        await AssertPolicyCaseAsync(catalog, timeProvider, liveCase.PolicyCase);
    }

    private async Task AssertPolicyCaseAsync(
        PolicyFixtureCatalog catalog,
        FakeTimeProvider timeProvider,
        PolicyAdversarialCase policyCase)
    {
        var invocation = CreateInvocation(catalog.FixtureDefaults, policyCase);
        var approvals = CreateApprovals(policyCase.Available);
        await using var harness = await ShellApprovalHarness.CreateAsync(
            policyCase.Id,
            invocation,
            approvals,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            timeProvider,
            new ShellApprovalHarnessScope(
                policyCase.ProjectDirectory,
                policyCase.SessionDirectory,
                catalog.FixtureDefaults.Session.SessionId,
                policyCase.Available.OneTimeApprovalKeys),
            policyCase.UseBundledSafeCatalog
                ? null
                : CreateSafeVerbs(policyCase.Available, invocation.CreateEnvironment()));

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{policyCase.Id} ({policyCase.Category}): outcome={decision.Outcome}; "
            + $"deny={decision.DenyReason}; "
            + $"candidates={string.Join(", ", decision.ApprovalContext?.CandidateVerbs ?? [])}; "
            + $"messy={decision.ApprovalContext?.IsMessy}; "
            + $"correction={decision.ApprovalContext?.AgentCorrection?.GetType().Name}; "
            + $"checks={harness.ApprovalService.CheckCount}; "
            + $"allow={decision.AllowReason}; "
            + $"matches={string.Join(", ", decision.ApprovalMatches.Select(item => item.Pattern))}; "
            + $"trace={string.Join(", ", decision.ShellPolicyTrace.Rows.Select(row => $"{row.Stage}/{row.Reason}"))}");

        Assert.Equal(ParseOutcome(policyCase.Expected.Outcome), decision.Outcome);
        Assert.Equal(policyCase.Expected.DenyReason, decision.DenyReason);
        Assert.Equal(
            policyCase.Expected.AgentCorrection,
            decision.ApprovalContext?.AgentCorrection?.GetType().Name);
        Assert.Equal(policyCase.Expected.ApprovalCandidates, decision.ApprovalContext?.CandidateVerbs);
        Assert.Equal(policyCase.Expected.IsMessy, decision.ApprovalContext?.IsMessy);
        Assert.Equal(
            policyCase.Expected.OptionKeys,
            decision.ApprovalContext?.Options.Select(option => option.Key.Value).ToList());
        Assert.Equal(policyCase.Expected.ActorCheckCount, harness.ApprovalService.CheckCount);
    }

    private static void AssertProjectedCandidates(
        PolicyFixtureCase policyCase,
        ShellApprovalInvocation invocation)
    {
        var environment = invocation.CreateEnvironment();
        var arguments = new Dictionary<string, object?>
        {
            ["Command"] = policyCase.Command,
            ["WorkingDirectory"] = policyCase.InitialWorkingDirectory,
        };
        var actual = new ShellApprovalMatcher(environment)
            .AnalyzeInvocation(new ToolName(ShellTool.ToolName), arguments)
            .Candidates;

        var hasCausalMetadata = policyCase.Candidates.Any(candidate => candidate.Role is not null);
        if (hasCausalMetadata)
        {
            Assert.All(policyCase.Candidates, candidate => Assert.NotNull(candidate.Role));
            var analysis = new ShellCommandAnalyzer(environment).Analyze(
                policyCase.Command,
                policyCase.InitialWorkingDirectory);
            AssertWorkingDirectoryEffects(policyCase, analysis);
            Assert.True(BashCausalApprovalIntent.TryProject(
                environment,
                analysis,
                new ShellApprovalMatcher(environment),
                PlatformTemporaryScopePolicy.Create(environment).IsSafePlatformTemporaryPath,
                out var causalCandidates));
            Assert.Equal(policyCase.Candidates.Count, causalCandidates.Count);
            for (var index = 0; index < policyCase.Candidates.Count; index++)
            {
                var expected = policyCase.Candidates[index];
                var candidate = causalCandidates[index];
                Assert.Equal(index, expected.Id);
                Assert.Equal(expected.Tokens, candidate.Candidate.VerbTokens);
                Assert.Equal(expected.RealDirectory, candidate.Candidate.Directory);
                Assert.Equal(expected.IntentDirectory, candidate.IntentDirectory);
                Assert.Equal(expected.Role, candidate.Role.ToString());
                Assert.Equal(expected.PrerequisiteIds ?? [], candidate.PrerequisiteIndexes);
            }

            return;
        }

        Assert.Equal(policyCase.Candidates.Count, actual.Count);
        for (var index = 0; index < policyCase.Candidates.Count; index++)
        {
            var expected = policyCase.Candidates[index];
            Assert.Equal(index, expected.Id);
            Assert.Equal(expected.Tokens, actual[index].VerbTokens);
            Assert.Equal(expected.RealDirectory, policyCase.InitialWorkingDirectory);
            Assert.Equal(
                expected.IntentDirectory ?? expected.RealDirectory,
                actual[index].Directory ?? policyCase.InitialWorkingDirectory);
        }
    }

    private static void AssertWorkingDirectoryEffects(
        PolicyFixtureCase policyCase,
        ShellCommandAnalysis analysis)
    {
        var expectedEffects = policyCase.ShellEffects?.WorkingDirectoryEffects ?? [];
        Assert.NotEmpty(expectedEffects);
        foreach (var expected in expectedEffects)
        {
            var effect = analysis.Commands[expected.CommandIndex].WorkingDirectoryEffect;
            switch (expected.Kind)
            {
                case "Unchanged":
                    Assert.IsType<ShellSyntaxTree.ShellWorkingDirectoryEffect.Unchanged>(effect);
                    Assert.Empty(expected.Targets);
                    break;
                case "ChangesOnSuccess":
                    var change = Assert.IsType<
                        ShellSyntaxTree.ShellWorkingDirectoryEffect.ChangesOnSuccess>(effect);
                    Assert.Equal(
                        Assert.Single(expected.Targets),
                        Assert.IsType<ShellSyntaxTree.ShellValueDomain.Exact>(change.Target)
                            .Value);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported working-directory effect: {expected.Kind}.");
            }
        }
    }

    private static ShellApprovalInvocation CreateInvocation(
        PolicyFixtureDefaults defaults,
        PolicyFixtureCase policyCase)
    {
        if (defaults.ToolName != "shell_execute"
            || defaults.ApprovalMode != "Approval"
            || defaults.PersistentStoreStatus != "Ready"
            || defaults.InheritedWorkingDirectory is not null
            || policyCase.Available.OneTimeApprovalKeys.Count != 0
            || policyCase.Environment.Grammar != "Bash"
            || policyCase.Environment.Platform != "Linux"
            || policyCase.Environment.ExecutablePath != "/bin/bash"
            || !policyCase.Environment.CommandArguments.SequenceEqual(["-c"])
            || policyCase.Environment.PathStyle != "Posix"
            || policyCase.Environment.PowerShellDialect is not null
            || policyCase.InitialWorkingDirectory != defaults.ProjectDirectory)
        {
            throw new InvalidDataException($"Unsupported fixture shape: {policyCase.EvidenceId}.");
        }

        return new ShellApprovalInvocation(
            policyCase.Command,
            ApprovalDirectoryShape.Project,
            Enum.Parse<TrustAudience>(defaults.Audience),
            defaults.InteractiveApprovalCapability == "Available");
    }

    private static ShellApprovalInvocation CreateInvocation(
        PolicyFixtureDefaults defaults,
        PolicyAdversarialCase policyCase)
    {
        if (defaults.ToolName != "shell_execute"
            || defaults.ApprovalMode != "Approval"
            || defaults.PersistentStoreStatus != "Ready"
            || defaults.InheritedWorkingDirectory is not null)
        {
            throw new InvalidDataException($"Unsupported fixture defaults: {policyCase.Id}.");
        }

        var host = policyCase.Environment switch
        {
            {
                Grammar: "Bash",
                Platform: "Linux",
                PathStyle: "Posix",
                ExecutablePath: "/bin/bash",
                PowerShellDialect: null
            } when policyCase.Environment.CommandArguments.SequenceEqual(["-c"])
                => ShellApprovalHost.Bash,
            {
                Grammar: "PowerShell",
                Platform: "Windows",
                PathStyle: "Windows",
                ExecutablePath: @"C:\Program Files\PowerShell\7\pwsh.exe",
                PowerShellDialect: "PowerShell7"
            } when policyCase.Environment.CommandArguments.SequenceEqual(
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"])
                => ShellApprovalHost.PowerShell7,
            {
                Grammar: "PowerShell",
                Platform: "Windows",
                PathStyle: "Windows",
                ExecutablePath: @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                PowerShellDialect: "WindowsPowerShell51"
            } when policyCase.Environment.CommandArguments.SequenceEqual(
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"])
                => ShellApprovalHost.WindowsPowerShell51,
            _ => throw new InvalidDataException($"Unsupported fixture environment: {policyCase.Id}.")
        };

        var pathStyle = host == ShellApprovalHost.Bash
            ? ShellPathStyle.Posix
            : ShellPathStyle.Windows;
        if (!string.Equals(
                policyCase.InitialWorkingDirectory,
                policyCase.ProjectDirectory,
                StringComparison.Ordinal)
            || !CanonicalShellPath.TryCreate(
                policyCase.ProjectDirectory,
                pathStyle,
                out var normalizedProjectDirectory)
            || !string.Equals(
                normalizedProjectDirectory.Value,
                policyCase.ProjectDirectory,
                StringComparison.Ordinal)
            || !CanonicalShellPath.TryCreate(
                policyCase.SessionDirectory,
                pathStyle,
                out var normalizedSessionDirectory)
            || !string.Equals(
                normalizedSessionDirectory.Value,
                policyCase.SessionDirectory,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported fixture scope: {policyCase.Id}.");
        }

        return new ShellApprovalInvocation(
            policyCase.Command,
            ApprovalDirectoryShape.Project,
            Enum.Parse<TrustAudience>(defaults.Audience),
            defaults.InteractiveApprovalCapability == "Available",
            host);
    }

    private static ApprovalState CreateApprovals(PolicyFixtureCase policyCase)
        => CreateApprovals(policyCase.Available);

    private static ApprovalState CreateApprovals(PolicyFixtureAuthority available)
        => new(available.PersistentGrants
            .Select(CreatePersistentSeed)
            .Concat(available.SessionGrants.Select(CreateSessionSeed))
            .ToList());

    private static SafeVerbList CreateSafeVerbs(
        PolicyFixtureAuthority available,
        ShellExecutionEnvironment environment)
    {
        if (available.SafePhrases.Any(phrase =>
                phrase.Proof != "ReviewedDiagnostic"))
        {
            throw new InvalidDataException("The fixture has an unsupported safe-phrase proof.");
        }

        return SafeVerbList.FromVerbs(
            environment.Grammar == ShellGrammar.Bash
                ? ApprovalShell.Bash
                : ApprovalShell.PowerShell,
            available.SafePhrases.Select(phrase =>
                string.Join(' ', phrase.Tokens)));
    }

    private static ApprovalSeed CreatePersistentSeed(PolicyGrant grant)
    {
        if (grant.Shell != "Bash" || grant.Match != "TokenPrefix")
            throw new InvalidDataException("The fixture has a noncanonical persistent grant.");

        var directory = grant.Kind switch
        {
            "PersistentGlobal" when grant.Directory is null => ApprovalDirectoryShape.None,
            "PersistentFolder" when grant.Directory == "/work" => ApprovalDirectoryShape.Project,
            _ => throw new InvalidDataException($"Unsupported persistent grant kind: {grant.Kind}.")
        };
        return new ApprovalSeed(
            ApprovalSeedSource.Persistent,
            string.Join(' ', grant.Tokens),
            TrustAudience.Personal,
            ApprovalSessionShape.Invocation,
            directory);
    }

    private static ApprovalSeed CreateSessionSeed(PolicyGrant grant)
    {
        if (grant.Kind != "Session" || grant.Shell != "Bash" || grant.Match != "TokenPrefix")
            throw new InvalidDataException("The fixture has a noncanonical session grant.");

        return new ApprovalSeed(
            ApprovalSeedSource.Session,
            string.Join(' ', grant.Tokens),
            TrustAudience.Personal,
            ApprovalSessionShape.Invocation,
            ApprovalDirectoryShape.None);
    }

    private static string FormatExpectedTraceRow(PolicyTraceRow row)
        => string.Join(
            '|',
            row.Stage,
            row.CandidateId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.ExecutableBasename ?? string.Empty,
            row.Outcome,
            row.Reason,
            row.Coverage ?? string.Empty,
            row.ScopeRelation ?? string.Empty,
            row.GrantTimestamp ?? string.Empty);

    private static string FormatActualTraceRow(ShellPolicyTraceRow row)
        => string.Join(
            '|',
            row.Stage,
            row.CandidateId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.ExecutableBasename ?? string.Empty,
            row.Outcome,
            row.Reason,
            row.Coverage?.ToString() ?? string.Empty,
            row.ScopeRelation,
            row.GrantTimestamp?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);

    private static ToolAuthorizationOutcome ParseOutcome(string outcome)
        => outcome switch
        {
            "Allow" => ToolAuthorizationOutcome.Allowed,
            "RequiresApproval" => ToolAuthorizationOutcome.RequiresApproval,
            "Deny" => ToolAuthorizationOutcome.Denied,
            _ => throw new InvalidDataException($"Unsupported fixture outcome: {outcome}.")
        };

    private static string EvidencePath()
        => Path.Combine(
            AppContext.BaseDirectory,
            "ApprovalEvidence",
            "netclaw-policy-fixtures.json");
}
