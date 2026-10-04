// -----------------------------------------------------------------------
// <copyright file="ShellPolicyEvidenceFixtureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Security.Tests;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellPolicyEvidenceFixtureTests(ShellApprovalMatrixFixture fixture) :
    IClassFixture<ShellApprovalMatrixFixture>
{
    private const string PolicyFixturesFile = "netclaw-policy-fixtures.json";
    private const string FreshSessionPolicyFixturesFile = "fresh-session-policy-fixtures.json";

    public static bool IsPosix => !OperatingSystem.IsWindows();

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
        var expectedOutcomes = new List<ApprovalOutcome>();
        var actualOutcomes = new List<ApprovalOutcome>();

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
            var observed = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{policyCase.EvidenceId}: outcome={observed.Outcome}; "
                + $"candidates={string.Join(", ", observed.Prompt?.CandidateVerbs ?? [])}; "
                + $"messy={observed.Prompt?.IsMessy}\n"
                + string.Join(Environment.NewLine, observed.TraceRows));

            expectedOutcomes.Add(ParseOutcome(policyCase.ExpectedFinal.Outcome));
            actualOutcomes.Add(observed.Outcome);
            Assert.Equal(
                policyCase.ExpectedFinal.ApprovalCandidates,
                observed.Prompt?.CandidateVerbs);
            Assert.Equal(policyCase.ExpectedFinal.IsMessy, observed.Prompt?.IsMessy);
            Assert.Equal(
                ParseCorrection(policyCase.ExpectedFinal.AgentCorrection),
                observed.AgentCorrection);
            expectedRows.AddRange(policyCase.ExpectedTrace.Select(row =>
                $"{policyCase.EvidenceId}|{FormatExpectedTraceRow(row)}"));
            actualRows.AddRange(observed.TraceRows.Select(row =>
                $"{policyCase.EvidenceId}|{row}"));
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
            await AssertPolicyCaseAsync(
                catalog,
                timeProvider,
                policyCase with { Expected = CurrentAdversarialExpected(policyCase) });
        }
    }

    // The archived A06 result kept the whole loop as one exact prompt. Each
    // command now gets its own decision (approval taxonomy PR 5): the global
    // cat grant covers cat "$f" under owner decision D1, and the iterator
    // program still prompts with normal options.
    private static PolicyAdversarialExpected CurrentAdversarialExpected(PolicyAdversarialCase policyCase)
        => policyCase.Id == "A06"
            ? policyCase.Expected with
            {
                ApprovalCandidates = ["list-files"],
                IsMessy = false,
                OptionKeys =
                [
                    ApprovalOptionKeys.ApproveOnce,
                    ApprovalOptionKeys.ApproveSession,
                    ApprovalOptionKeys.ApproveEverywhere,
                    ApprovalOptionKeys.Deny
                ],
                ActorCheckCount = 1
            }
            : policyCase.Expected;

    public static TheoryData<string> LiveRegressionCaseIds => new(
        Enumerable.Range(1, 32).Select(number => $"L{number:00}"));

    public static TheoryData<string> FreshSessionRegressionCaseIds => new(
        Enumerable.Range(1, 10).Select(number => $"R{number:00}"));

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
        var policyCase = liveCase.PolicyCase with
        {
            Expected = CurrentStaticScopeExpected(liveCase.PolicyCase)
        };
        await AssertPolicyCaseAsync(catalog, timeProvider, policyCase);
    }

    private static PolicyAdversarialExpected CurrentStaticScopeExpected(
        PolicyAdversarialCase policyCase)
    {
        // The archived fixture keeps the prior exact-only result.
        // These rows state the current finite-scope contract. In an
        // interactive run on a POSIX host, the reviewed catalog covers a cd and
        // a read in each directory that the audience may read, so no row lists
        // cd there. A Windows host keeps the project-root results.
        // The reviewed catalog lists sort, so L05 needs no prompt. In an
        // interactive run a reviewed phrase covers each path that the audience
        // may read, so the external reads in L18, L21, and L28 need no prompt.
        // The fixtures use POSIX paths. Only a POSIX host can read them, so a
        // Windows host keeps the archived project-root results.
        var posixHost = !OperatingSystem.IsWindows();
        if (policyCase.Id is "L05" || posixHost && policyCase.Id is "L18" or "L21" or "L28")
        {
            return policyCase.Expected with
            {
                Outcome = "Allow",
                ApprovalCandidates = null,
                IsMessy = null,
                OptionKeys = null,
                ActorCheckCount = 1
            };
        }

        // Each command gets its own decision (approval taxonomy PR 5). An
        // unresolved command is one exact candidate with only "Once" and "Deny".
        // In L27 the dynamic -C value comes before the verb, so its command
        // words are unknown, and the model gets a rewrite correction.
        if (policyCase.Id == "L27")
        {
            return policyCase.Expected with
            {
                Outcome = "RequiresAgentCorrection",
                AgentCorrection = "ShellCommandWordsRewriteSuggested",
                ApprovalCandidates = null,
                IsMessy = null,
                OptionKeys = null,
                ActorCheckCount = 1
            };
        }

        // Host facts change the lists. The Windows bundled catalog has no cd or
        // grep entry, so those commands stay in the prompt. On macOS, /tmp is a
        // link, so a command that writes below /tmp stays exact. The macOS host
        // also gives no proved scope to any L17 command, so each one is exact.
        const string sedRange = "sed -n \"$(grep -n 'FAIL' /tmp/test.log | cut -d: -f1),+4p\" /tmp/test.log";
        var windowsHost = OperatingSystem.IsWindows();
        var macHost = OperatingSystem.IsMacOS();
        List<string>? exactCandidates = policyCase.Id switch
        {
            "L10" when windowsHost => ["dotnet test", "grep", sedRange],
            "L10" when macHost => ["dotnet test > /tmp/test.log", sedRange],
            "L10" => ["dotnet test", sedRange],
            "L13" when windowsHost => ["cd", "ls -la \"$project\"", "head"],
            "L13" => ["ls -la \"$project\"", "head"],
            "L17" when macHost =>
            [
                "git diff --name-only origin/dev...HEAD",
                "sort > /tmp/old-files",
                "git diff --name-only origin/dev...feature/example",
                "sort > /tmp/new-files",
                "comm /tmp/old-files /tmp/new-files"
            ],
            _ => null
        };
        if (exactCandidates is not null)
        {
            return policyCase.Expected with
            {
                ApprovalCandidates = exactCandidates,
                IsMessy = false,
                OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
                ActorCheckCount = 1
            };
        }

        // The Windows bundled catalog has no cd entry, so L22 keeps cd on a Windows host.
        // The catalog lists git diff, git show, and sort. In L17 each sort writes a
        // file outside the project, so it still prompts.
        List<string>? candidates = policyCase.Id switch
        {
            "L12" => posixHost ? ["mkdir", "git clone"] : ["mkdir", "cd", "git clone"],
            "L14" => ["git remote", "git fetch origin", "git fetch upstream"],
            "L15" => posixHost ? ["find"] : ["cd", "find", "head"],
            "L16" => ["git add", "git rebase"],
            "L17" => ["sort", "comm"],
            "L18" => ["cd", "ls", "head"],
            "L21" => ["cd", "git log", "grep"],
            "L22" => windowsHost ? ["cd", "python3"] : ["python3"],
            "L24" when posixHost => ["external-crm deals list"],
            "L29" => ["docker compose config"],
            "L30" => ["sed"],
            "L32" => ["gh api"],
            _ => null
        };
        if (candidates is null)
            return policyCase.Expected;

        var optionKeys = new List<string>
        {
            ApprovalOptionKeys.ApproveOnce,
            ApprovalOptionKeys.ApproveSession
        };

        if (policyCase.Id is "L18" or "L21")
            optionKeys.Add(ApprovalOptionKeys.ApproveAlways);
        optionKeys.Add(ApprovalOptionKeys.ApproveEverywhere);
        optionKeys.Add(ApprovalOptionKeys.Deny);

        return policyCase.Expected with
        {
            ApprovalCandidates = candidates,
            IsMessy = false,
            OptionKeys = optionKeys,
            ActorCheckCount = 1
        };
    }

    [SlopwatchSuppress(
        "SW001",
        "The corpus declares Linux session paths that cannot represent native Windows storage roots.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The corpus declares Linux session paths")]
    [MemberData(nameof(FreshSessionRegressionCaseIds))]
    public async Task Fresh_session_regression_fixtures_pin_current_policy_outcomes(string caseId)
    {
        var catalog = JsonSerializer.Deserialize(
                          File.ReadAllBytes(EvidencePath(FreshSessionPolicyFixturesFile)),
                          ShellPolicyFixtureJsonContext.Default.PolicyFixtureCatalog)
                      ?? throw new InvalidDataException("The fresh-session fixture catalog has no root object.");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse(
            catalog.FixtureDefaults.ClockUtc,
            CultureInfo.InvariantCulture));

        var liveCase = Assert.Single(
            catalog.LiveRegressionCases,
            item => item.PolicyCase.Id == caseId);
        var policyCase = liveCase.PolicyCase;
        // The archived R05 result prompts for cat and rg outside the project.
        // An interactive reviewed phrase now covers each path that the
        // audience may read, so the call needs no prompt.
        if (policyCase.Id == "R05")
        {
            policyCase = policyCase with
            {
                Expected = policyCase.Expected with
                {
                    Outcome = "Allow",
                    ApprovalCandidates = null,
                    IsMessy = null,
                    OptionKeys = null,
                    ActorCheckCount = 1,
                    CandidateCoverage = null,
                    Trace = null
                }
            };
        }

        await AssertPolicyCaseAsync(catalog, timeProvider, policyCase);
    }

    private async Task AssertPolicyCaseAsync(
        PolicyFixtureCatalog catalog,
        FakeTimeProvider timeProvider,
        PolicyAdversarialCase policyCase)
    {
        var invocation = CreateInvocation(catalog.FixtureDefaults, policyCase);
        var approvals = CreateApprovals(policyCase.Available);
        var environment = invocation.CreateEnvironment();
        var materializeFileSystemFacts = policyCase.UsePhysicalHarnessScope
                                         && CanonicalPath.IsHostPathStyle(
                                             environment.PathStyle);
        var scope = materializeFileSystemFacts
            ? null
            : new ShellApprovalHarnessScope(
                policyCase.ProjectDirectory,
                policyCase.SessionDirectory,
                catalog.FixtureDefaults.Session.SessionId,
                policyCase.Available.OneTimeApprovalKeys);
        await using var harness = await ShellApprovalHarness.CreateAsync(
            policyCase.Id,
            invocation,
            approvals,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            timeProvider,
            scope,
            policyCase.UseBundledSafeCatalog
                ? null
                : CreateSafeVerbs(policyCase.Available, environment),
            policyCase.DeniedPaths);
        ApplyFileSystemFacts(policyCase, harness, materializeFileSystemFacts);

        var observed = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{policyCase.Id} ({policyCase.Category}): outcome={observed.Outcome}; "
            + $"deny={observed.DenyReason}; "
            + $"candidates={string.Join(", ", observed.Prompt?.CandidateVerbs ?? [])}; "
            + $"messy={observed.Prompt?.IsMessy}; "
            + $"options={string.Join(",", observed.Prompt?.OptionKeys ?? [])}; "
            + $"correction={observed.AgentCorrection}; "
            + $"checks={observed.ApprovalChecks}; "
            + $"allow={observed.AllowReason}; "
            + $"matches={string.Join(", ", observed.ApprovalMatches)}; "
            + "trace:\n"
            + string.Join(Environment.NewLine, observed.TraceRows));

        Assert.Equal(ParseOutcome(policyCase.Expected.Outcome), observed.Outcome);
        Assert.Equal(policyCase.Expected.DenyReason, observed.DenyReason);
        Assert.Equal(
            ParseCorrection(policyCase.Expected.AgentCorrection),
            observed.AgentCorrection);
        Assert.Equal(policyCase.Expected.ApprovalCandidates, observed.Prompt?.CandidateVerbs);
        Assert.Equal(policyCase.Expected.IsMessy, observed.Prompt?.IsMessy);
        Assert.Equal(policyCase.Expected.OptionKeys, observed.Prompt?.OptionKeys);
        Assert.Equal(policyCase.Expected.ActorCheckCount, observed.ApprovalChecks);
        if (policyCase.Expected.CandidateCoverage is { } expectedCoverage)
        {
            Assert.Equal(
                expectedCoverage.Select(item => (item.CandidateId, item.Coverage)),
                observed.CandidateCoverage);
        }

        if (policyCase.Expected.Trace is { } expectedTrace)
        {
            Assert.Equal(
                expectedTrace.Select(FormatExpectedTraceRow),
                observed.TraceRows);
        }
    }

    private static void ApplyFileSystemFacts(
        PolicyAdversarialCase policyCase,
        ShellApprovalHarness harness,
        bool materializeFileSystemFacts)
    {
        foreach (var fact in policyCase.FileSystemFacts ?? [])
        {
            if (fact.Kind != "ProjectFileSymlink" || !policyCase.UsePhysicalHarnessScope)
            {
                throw new InvalidDataException(
                    $"Unsupported fixture filesystem fact: {policyCase.Id}/{fact.Kind}.");
            }

            if (materializeFileSystemFacts)
                harness.CreateProjectFileSymlink(fact.Path, fact.Target);
        }
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
            var policy = new ShellCommandPolicy(environment);
            var analysis = policy.Analyze(
                policyCase.Command,
                policyCase.InitialWorkingDirectory);
            AssertWorkingDirectoryEffects(policyCase, analysis);
            Assert.True(BashDirectoryScopeProjection.TryCreate(
                analysis,
                policy,
                new ShellApprovalMatcher(environment),
                out var projection));
            Assert.True(projection.IsCausalList);
            var scoped = projection.Slices
                .SelectMany(static slice => slice.Approval.Candidates.Select(candidate => (candidate, slice)))
                .ToArray();
            Assert.Equal(policyCase.Candidates.Count, scoped.Length);
            for (var index = 0; index < policyCase.Candidates.Count; index++)
            {
                var expected = policyCase.Candidates[index];
                var (candidate, slice) = scoped[index];
                Assert.Equal(index, expected.Id);
                Assert.Equal(expected.Tokens, candidate.VerbTokens);
                Assert.Equal(expected.RealDirectory, candidate.Directory);
                Assert.Equal(expected.IntentDirectory, slice.IntentDirectory);
                Assert.Equal(
                    expected.Role,
                    slice.IntentDirectory is null
                        ? nameof(ShellPolicyCandidateRole.CausalPrerequisite)
                        : nameof(ShellPolicyCandidateRole.CausalIntentConsumer));
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
        if (!CanonicalPath.TryCreate(
                policyCase.ProjectDirectory,
                relativeBase: null,
                pathStyle,
                out var normalizedProjectDirectory)
            || !string.Equals(
                normalizedProjectDirectory.Value,
                policyCase.ProjectDirectory,
                StringComparison.Ordinal)
            || !CanonicalPath.TryCreate(
                policyCase.SessionDirectory,
                relativeBase: null,
                pathStyle,
                out var normalizedSessionDirectory)
            || !string.Equals(
                normalizedSessionDirectory.Value,
                policyCase.SessionDirectory,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported fixture scope: {policyCase.Id}.");
        }


        var directoryShape = policyCase.InitialWorkingDirectory switch
        {
            var directory when string.Equals(
                directory,
                policyCase.ProjectDirectory,
                StringComparison.Ordinal) => ApprovalDirectoryShape.Project,
            var directory when string.Equals(
                directory,
                policyCase.SessionDirectory,
                StringComparison.Ordinal) => ApprovalDirectoryShape.Session,
            _ => throw new InvalidDataException(
                $"Unsupported initial working directory: {policyCase.Id}.")
        };

        return new ShellApprovalInvocation(
            policyCase.Command,
            directoryShape,
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

    private static ApprovalOutcome ParseOutcome(string outcome)
        => outcome switch
        {
            "Allow" => ApprovalOutcome.Allowed,
            "RequiresApproval" => ApprovalOutcome.RequiresApproval,
            "Deny" => ApprovalOutcome.Denied,
            "RequiresAgentCorrection" => ApprovalOutcome.RequiresAgentCorrection,
            _ => throw new InvalidDataException($"Unsupported fixture outcome: {outcome}.")
        };

    private static ApprovalCorrection? ParseCorrection(string? correction)
        => correction switch
        {
            null => null,
            "ManagedTemporaryDirectorySuggested" => ApprovalCorrection.ManagedTemporaryDirectory,
            "NativeToolSuggested" => ApprovalCorrection.NativeTool,
            "ProjectDirectorySuggested" => ApprovalCorrection.ProjectDirectory,
            "ShellWorkingDirectorySuggested" => ApprovalCorrection.ShellWorkingDirectory,
            "ShellCommandWordsRewriteSuggested" => ApprovalCorrection.ShellCommandWords,
            _ => throw new InvalidDataException($"Unsupported fixture correction: {correction}.")
        };

    private static string EvidencePath(string fileName = PolicyFixturesFile)
        => Path.Combine(
            AppContext.BaseDirectory,
            "ApprovalEvidence",
            fileName);
}
