// -----------------------------------------------------------------------
// <copyright file="ReviewedSafeShellPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ReviewedSafeShellPolicyTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _projectDir;
    private readonly string _undeclaredProjectDir;
    private readonly string _sessionDir;
    private readonly string _outsideDir;
    private readonly NetclawPaths _paths;

    public ReviewedSafeShellPolicyTests()
    {
        _rootDir = CreateTempDir("policy");
        _paths = new NetclawPaths(_rootDir);
        _projectDir = Path.Combine(_paths.WorkspacesDirectory, "project");
        _undeclaredProjectDir = Path.Combine(_paths.WorkspacesDirectory, "undeclared");
        _sessionDir = Path.Combine(_paths.SessionsDirectory, "session");
        _outsideDir = CreateTempDir("outside");
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_undeclaredProjectDir);
        Directory.CreateDirectory(_sessionDir);
    }

    public void Dispose()
    {
        SafeDelete(_rootDir);
        SafeDelete(_outsideDir);
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netclaw-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDelete(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort — a leftover temp tree from a failed
            // test run is acceptable, a test crash from a permissions glitch
            // during teardown is not. Log so an investigator finding the
            // leftover tree can correlate it back to a specific test run.
            System.Diagnostics.Debug.WriteLine($"SafeDelete failed for '{path}': {ex.Message}");
        }
    }

    private static SafeVerbList VerbList(params string[] verbs)
        => SafeVerbList.FromVerbs(ApprovalShell.Bash, verbs);

    private ReviewedSafeShellPolicy CreatePolicy(SafeVerbList safeVerbs)
        => new(
            safeVerbs,
            new PathAccessPolicy(
                new ToolConfig(),
                _paths,
                new ToolPathPolicy([])));

    // R12 (session and project roots only) holds when the audience profile
    // cannot read the path. A Personal profile with no read roots shows it. It
    // applies to attended and unattended runs alike (D2).
    private ReviewedSafeShellPolicy CreateBoundedPolicy(SafeVerbList safeVerbs)
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.Roots,
            Roots = []
        };
        return new(safeVerbs, new PathAccessPolicy(config, _paths, new ToolPathPolicy([])));
    }

    private static ApprovalCandidate Candidate(
        string verb,
        string? directory = null,
        ApprovalShell shell = ApprovalShell.Bash)
    {
        if (shell != ApprovalShell.Bash)
        {
            return new ApprovalCandidate(verb, directory)
            {
                Shell = shell,
                VerbTokens = Array.AsReadOnly(
                    verb.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            };
        }

        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var parsed = Assert.Single(matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = verb,
                ["WorkingDirectory"] = "/"
            }));
        return parsed with { Directory = directory };
    }

    private static IReadOnlyList<ApprovalCandidate> Candidates(params string[] verbs)
        => verbs.Select(verb => Candidate(verb)).ToList();

    private static bool ShortCircuits(
        ReviewedSafeShellPolicy policy,
        string verb,
        string? cwd,
        ToolInvocationContext context) =>
        AllShortCircuit(policy, [Candidate(verb)], cwd, context);

    private static bool AllShortCircuit(
        ReviewedSafeShellPolicy policy,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        ToolInvocationContext context)
    {
        if (candidates.Count == 0)
            return false;

        return candidates.All(candidate => policy.ShortCircuits(candidate, cwd, context));
    }

    private ToolInvocationContext PersonalContext(string? projectDir = null, string? sessionDir = null)
        => TestToolExecutionContext.CreateBound("session-1", sessionDir ?? _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = projectDir
        }).Invocation;

    private ToolInvocationContext UnattendedPersonalContext(string? projectDir = null)
        => TestToolExecutionContext.CreateBound("session-1", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = projectDir,
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
        }).Invocation;

    private ToolInvocationContext PublicContext(string? projectDir = null)
        => TestToolExecutionContext.CreateBound("session-1", _sessionDir, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Public,
            ProjectDirectory = projectDir
        }).Invocation;

    [Fact]
    public void Safe_verb_in_project_directory_short_circuits()
    {
        var policy = CreatePolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(ShortCircuits(policy, "grep", _projectDir, ctx));
    }

    [Fact]
    public void Assignment_qualified_safe_verb_does_not_short_circuit()
    {
        var policy = CreatePolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var parsed = Candidate("grep", _projectDir);
        var digest = new ApprovalAssignmentDigest($"sha256:{new string('a', 64)}");
        var qualified = parsed with
        {
            AssignmentDigest = digest,
        };

        Assert.False(AllShortCircuit(policy, [qualified], _projectDir, ctx));
        Assert.False(policy.IsReviewedDiagnosticInvocation(
            [qualified],
            ShellPathStyle.Posix));
    }

    [Fact]
    public void Safe_verb_in_session_directory_short_circuits()
    {
        var policy = CreatePolicy(VerbList("cat"));
        var ctx = PersonalContext();

        Assert.True(ShortCircuits(policy, "cat", _sessionDir, ctx));
    }

    [Fact]
    public void Reviewed_safe_policy_rejects_exact_only_tree_access()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PwshDialect.WindowsPowerShell51);
        var occurrence = Assert.Single(
            new ShellCommandAnalyzer(environment)
                .Analyze(
                    @"Get-ChildItem -Path C:\WORK\PROJECT -Recurse",
                    @"C:\WORK\PROJECT")
                .Commands);
        var candidate = new ApprovalCandidate("Get-ChildItem", @"C:\WORK\PROJECT")
        {
            Shell = ApprovalShell.PowerShell,
            VerbTokens = ["Get-ChildItem"],
            SourceOccurrence = occurrence
        };
        var projected = new ShellPolicyCandidate(
            new ShellPolicyCandidateId(0),
            candidate,
            occurrence);
        var facts = Assert.Single(ShellPolicyPathFacts.Create(
            [projected],
            ShellPathStyle.Windows));
        var policy = CreatePolicy(SafeVerbList.FromVerbs(
            ApprovalShell.PowerShell,
            ["Get-ChildItem"]));

        Assert.False(policy.ShortCircuits(
            projected,
            facts,
            PersonalContext(projectDir: _projectDir)));
    }

    [Fact]
    public void Reviewed_verb_in_session_worktree_directory_uses_shared_session_trusted_root()
    {
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(_sessionDir)));
        Directory.CreateDirectory(storage.WorktreeDirectory.Value);
        var context = TestToolExecutionContext.CreateBoundWithStorage(
            "session-1",
            storage,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal }).Invocation;
        var policy = CreatePolicy(VerbList("cat"));

        Assert.True(ShortCircuits(policy, "cat", storage.WorktreeDirectory.Value, context));
    }

    [Fact]
    public void Git_worktree_creation_uses_normal_shell_authorization()
    {
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(_sessionDir)));
        var context = TestToolExecutionContext.CreateBoundWithStorage(
            "session-1",
            storage,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal }).Invocation;
        var policy = CreatePolicy(VerbList("git worktree list"));

        Assert.False(ShortCircuits(
            policy,
            $"git worktree add {Path.Combine(storage.WorktreeDirectory.Value, "fix")}",
            storage.SessionDirectory.Value,
            context));
    }

    [Fact]
    public void Bounded_reviewed_verb_outside_trusted_roots_falls_through_to_prompt()
    {
        var policy = CreateBoundedPolicy(VerbList("grep"));
        var ctx = UnattendedPersonalContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "grep", _outsideDir, ctx));
    }

    [Fact]
    public void Mutating_verb_inside_trusted_root_falls_through_to_prompt()
    {
        // The verb list deliberately omits "git push"; even with cwd inside
        // the trusted root, the policy refuses the short-circuit.
        var policy = CreatePolicy(VerbList("git status", "git log"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "git push", _projectDir, ctx));
    }

    [Fact]
    public void Public_audience_does_not_get_project_directory_authority()
    {
        var policy = CreatePolicy(VerbList("grep"));
        // Public has project_dir set (somehow), but it should be ignored.
        var ctx = PublicContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "grep", _projectDir, ctx));
        // Session_dir still works for Public.
        Assert.True(ShortCircuits(policy, "grep", _sessionDir, ctx));
    }

    [Fact]
    public void Bounded_symlink_segment_in_cwd_breaks_short_circuit()
    {
        // Skip on Windows where directory symlink creation is privilege-gated.
        if (OperatingSystem.IsWindows())
            return;

        var leakTarget = CreateTempDir("leak-target");
        var symlinkPath = Path.Combine(_projectDir, "leak");
        try
        {
            Directory.CreateSymbolicLink(symlinkPath, leakTarget);

            var policy = CreateBoundedPolicy(VerbList("cat"));
            var ctx = UnattendedPersonalContext(projectDir: _projectDir);

            Assert.False(ShortCircuits(policy, "cat", symlinkPath, ctx));
        }
        finally
        {
            SafeDelete(leakTarget);
        }
    }

    [Fact]
    public void All_short_circuit_returns_false_when_any_verb_is_unsafe()
    {
        var policy = CreatePolicy(VerbList("grep", "cat"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(policy, Candidates("grep", "git push"), _projectDir, ctx));
    }

    [Fact]
    public void All_short_circuit_returns_true_when_every_verb_is_safe_and_in_space()
    {
        var policy = CreatePolicy(VerbList("grep", "cat", "wc"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(AllShortCircuit(policy, Candidates("grep", "cat", "wc"), _projectDir, ctx));
    }

    [Fact]
    public void Empty_candidate_list_does_not_short_circuit()
    {
        var policy = CreatePolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(policy, [], _projectDir, ctx));
    }

    [Fact]
    public void Bounded_null_cwd_does_not_short_circuit()
    {
        var policy = CreateBoundedPolicy(VerbList("grep"));
        var ctx = UnattendedPersonalContext(projectDir: _projectDir);

        Assert.False(ShortCircuits(policy, "grep", null, ctx));
    }

    [Fact]
    public void Newly_added_read_only_verb_short_circuits_inside_trusted_root()
    {
        // Mirrors the reviewed catalog: a read-only system verb and a
        // read-only gh query short-circuit inside a trusted
        // zone and still prompt outside one. The bundled list's membership of
        // these verbs is verified separately by SafeVerbLoaderTests.
        var policy = CreatePolicy(VerbList("whoami", "gh run list"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(ShortCircuits(policy, "whoami", _sessionDir, ctx));
        Assert.True(ShortCircuits(policy, "gh run list", _projectDir, ctx));
        Assert.False(ShortCircuits(CreateBoundedPolicy(VerbList("whoami")), "whoami", _outsideDir, UnattendedPersonalContext(projectDir: _projectDir)));
    }

    [Fact]
    public void New_safe_verb_chained_with_mutating_verb_still_prompts()
    {
        // The all-clauses-safe conjunction holds: `whoami` is safe but a
        // compound that also runs the unlisted `git push` must still prompt.
        var policy = CreatePolicy(VerbList("whoami"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(
            policy,
            Candidates("whoami", "git push origin main"),
            _projectDir,
            ctx));
    }

    [Fact]
    public void Reviewed_phrase_matches_a_longer_canonical_token_chain()
    {
        var policy = CreatePolicy(VerbList("git ls-tree"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.True(AllShortCircuit(
            policy,
            [Candidate("git ls-tree feature", _projectDir)],
            _projectDir,
            ctx));
    }

    [Theory]
    [InlineData("git -c include.path={0}/config status")]
    [InlineData("git --no-pager status")]
    public void Argument_before_reviewed_phrase_stays_strict(string commandTemplate)
    {
        var policy = CreatePolicy(VerbList("git status"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var command = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            commandTemplate,
            _outsideDir);
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = _projectDir
            });

        Assert.False(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Theory]
    [InlineData("grep -f /external/patterns ./data.txt", "grep")]
    [InlineData("wc --files0-from=/external/list", "wc")]
    [InlineData("du --exclude-from=/external/patterns ./data", "du")]
    [InlineData("realpath --relative-to=/external ./data", "realpath")]
    public void Bounded_path_shaped_option_operand_outside_trusted_roots_stays_strict(
        string command,
        string phrase)
    {
        var policy = CreateBoundedPolicy(VerbList(phrase));
        var ctx = UnattendedPersonalContext(projectDir: _projectDir);
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = _projectDir
            });

        Assert.False(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Path_shaped_option_operand_under_trusted_root_remains_eligible()
    {
        var policy = CreatePolicy(VerbList("grep"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var command = "grep -f ./patterns ./data.txt";
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = _projectDir
            });

        Assert.True(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Path_shaped_data_under_trusted_root_does_not_create_new_authority()
    {
        var policy = CreatePolicy(VerbList("gh run list"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var matcher = new ShellApprovalMatcher(ShellExecutionEnvironmentDefaults.Bash);
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "gh run list --repo example/project",
                ["WorkingDirectory"] = _projectDir
            });

        Assert.True(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void PowerShell_compatibility_paths_use_posix_host_roots()
    {
        if (OperatingSystem.IsWindows())
            return;

        var policy = CreatePolicy(
            SafeVerbList.FromVerbs(ApprovalShell.PowerShell, ["Get-ChildItem"]));
        var ctx = PersonalContext(projectDir: _projectDir);
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                "C:\\PowerShell\\pwsh.exe",
                PwshDialect.PowerShell7));
        var candidates = matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "Get-ChildItem -LiteralPath .\\data.txt",
                ["WorkingDirectory"] = _projectDir
            });

        Assert.True(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Prefix_collision_does_not_match_reviewed_phrase()
    {
        var policy = CreatePolicy(VerbList("git ls-tree"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(
            policy,
            [Candidate("git ls-treex feature", _projectDir)],
            _projectDir,
            ctx));
    }

    [Fact]
    public void Candidate_without_canonical_tokens_stays_strict()
    {
        var policy = CreatePolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidate = new ApprovalCandidate("head", _projectDir);

        Assert.False(AllShortCircuit(policy, [candidate], _projectDir, ctx));
    }

    [Fact]
    public void Candidate_from_another_shell_stays_strict()
    {
        var policy = CreatePolicy(VerbList("Get-Content"));
        var ctx = PersonalContext(projectDir: _projectDir);

        Assert.False(AllShortCircuit(
            policy,
            [Candidate("Get-Content", _projectDir, ApprovalShell.PowerShell)],
            _projectDir,
            ctx));
    }

    [Fact]
    public void Bounded_dotted_symlink_directory_does_not_short_circuit()
    {
        if (OperatingSystem.IsWindows())
            return;

        var target = CreateTempDir("dotted-target");
        var link = Path.Combine(_projectDir, "service.repo");
        try
        {
            Directory.CreateSymbolicLink(link, target);
            var policy = CreateBoundedPolicy(VerbList("find"));
            var ctx = UnattendedPersonalContext(projectDir: _projectDir);
            var candidate = Candidate("find", link);

            Assert.False(AllShortCircuit(policy, [candidate], _projectDir, ctx));
        }
        finally
        {
            SafeDelete(target);
        }
    }

    [Fact]
    public void Causal_intent_accepts_an_already_validated_root_alias()
    {
        if (OperatingSystem.IsWindows())
            return;

        var target = CreateTempDir("causal-target");
        var alias = Path.Combine(_outsideDir, "causal-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, target);
            // The intent root is a link that an earlier stage already accepted.
            // Build the consumer candidate the way causal projection does, so
            // this case checks only the reviewed-safe root rule (R3).
            var environment = ShellExecutionEnvironmentDefaults.Bash;
            var command = $"cd {alias} && inspect > result.log 2>&1; head result.log";
            var analysis = new ShellCommandAnalyzer(environment).Analyze(command, _projectDir);
            var consumer = analysis.Commands[^1];
            var candidate = Assert.Single(new ShellApprovalMatcher(environment).ExtractCandidatesForOccurrence(
                consumer,
                alias,
                resolveUnknownPathsFromEffectiveValues: true,
                LinkRule.FromVolumeRoot)!);
            var projected = new ShellPolicyCandidate(
                new ShellPolicyCandidateId(0),
                candidate with { Directory = null, SourceOccurrence = null },
                consumer)
            {
                Role = ShellPolicyCandidateRole.CausalIntentConsumer,
                IntentDirectory = alias
            };
            var facts = Assert.Single(ShellPolicyPathFacts.Create(
                [projected],
                ShellPathStyle.Posix));
            var policy = CreatePolicy(VerbList("head"));

            Assert.True(policy.ShortCircuitsCausalIntent(
                projected,
                facts,
                PersonalContext(projectDir: _projectDir)));
        }
        finally
        {
            SafeDelete(target);
        }
    }

    [Fact]
    public void Global_read_root_gets_the_same_reviewed_safe_coverage_attended_and_unattended()
    {
        // D2: a reviewed phrase covers each path that the audience profile lets
        // a file tool read, attended or not. A global read root is such a path.
        var skillDirectory = Path.Combine(_paths.SkillsDirectory, "example");
        Directory.CreateDirectory(skillDirectory);
        var policy = CreatePolicy(VerbList("cat"));

        Assert.True(AllShortCircuit(policy, [Candidate("cat", skillDirectory)], skillDirectory, PersonalContext(projectDir: _projectDir)));
        Assert.True(AllShortCircuit(policy, [Candidate("cat", skillDirectory)], skillDirectory, UnattendedPersonalContext(projectDir: _projectDir)));
    }

    [Fact]
    public void Bounded_candidate_path_outside_trusted_roots_falls_through_to_prompt()
    {
        var policy = CreateBoundedPolicy(VerbList("cat"));
        var ctx = UnattendedPersonalContext(projectDir: _projectDir);
        var candidates = new[] { Candidate("cat", _outsideDir) };

        Assert.False(AllShortCircuit(policy, candidates, _projectDir, ctx));
    }

    [Fact]
    public void Reviewed_safe_work_under_a_readable_cwd_needs_no_project_declaration()
    {
        // The Bash candidates use POSIX paths, which the read decision can
        // judge only on a POSIX host.
        if (OperatingSystem.IsWindows())
            return;

        var nested = Path.Combine(_undeclaredProjectDir, "src");
        Directory.CreateDirectory(nested);
        var candidates = new[]
        {
            Candidate("head", nested),
            Candidate("wc", _undeclaredProjectDir)
        };
        var policy = CreatePolicy(VerbList("head", "wc"));

        // The default Personal profile may read the cwd, attended or not (D2),
        // so the reviewed phrase covers the call with no declaration.
        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(
            candidates,
            _undeclaredProjectDir,
            PersonalContext(projectDir: _projectDir)));
        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(
            candidates,
            _undeclaredProjectDir,
            UnattendedPersonalContext(projectDir: _projectDir)));
    }

    [Fact]
    public void Already_declared_project_scope_does_not_request_another_declaration()
    {
        var policy = CreatePolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _outsideDir);
        var candidates = new[] { Candidate("head", _outsideDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Unsafe_work_cannot_request_project_declaration()
    {
        var policy = CreatePolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[]
        {
            Candidate("head", _outsideDir),
            Candidate("rm", _outsideDir)
        };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Explicit_path_outside_cwd_cannot_request_project_declaration()
    {
        var policy = CreatePolicy(VerbList("head"));
        var ctx = PersonalContext(projectDir: _projectDir);
        var candidates = new[] { Candidate("head", _projectDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    [Fact]
    public void Public_session_cannot_request_project_declaration()
    {
        var policy = CreatePolicy(VerbList("head"));
        var ctx = PublicContext(projectDir: _projectDir);
        var candidates = new[] { Candidate("head", _outsideDir) };

        Assert.False(policy.CanShortCircuitAfterProjectDeclaration(candidates, _outsideDir, ctx));
    }

    public enum ReadReach
    {
        AllFiles,
        ProjectRootOnly,
        Unattended,
        PublicAudience,
    }

    [Theory]
    [InlineData(ReadReach.AllFiles, "outside", true)]
    [InlineData(ReadReach.AllFiles, "protected", false)]
    [InlineData(ReadReach.ProjectRootOnly, "outside", false)]
    [InlineData(ReadReach.ProjectRootOnly, "project", true)]
    [InlineData(ReadReach.Unattended, "outside", true)]
    [InlineData(ReadReach.PublicAudience, "outside", false)]
    public void Reviewed_phrase_covers_each_path_the_audience_may_read(
        ReadReach reach,
        string target,
        bool expected)
    {
        // The Bash candidates use POSIX paths, which the read decision can
        // judge only on a POSIX host.
        if (OperatingSystem.IsWindows())
            return;

        var protectedDirectory = Path.Combine(_outsideDir, "secrets");
        Directory.CreateDirectory(protectedDirectory);
        var config = new ToolConfig();
        if (reach == ReadReach.ProjectRootOnly)
        {
            config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
            {
                Mode = ToolFilesystemMode.Roots,
                Roots = [_projectDir]
            };
        }

        var policy = new ReviewedSafeShellPolicy(
            VerbList("cat"),
            new PathAccessPolicy(config, _paths, new ToolPathPolicy([protectedDirectory])));
        var context = reach switch
        {
            ReadReach.Unattended => UnattendedPersonalContext(projectDir: _projectDir),
            ReadReach.PublicAudience => PublicContext(projectDir: _projectDir),
            _ => PersonalContext(projectDir: _projectDir)
        };
        var directory = target switch
        {
            "protected" => protectedDirectory,
            "project" => _projectDir,
            _ => _outsideDir
        };

        Assert.Equal(expected, AllShortCircuit(policy, [Candidate("cat", directory)], directory, context));
    }
}
