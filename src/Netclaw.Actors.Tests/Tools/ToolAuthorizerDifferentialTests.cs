// -----------------------------------------------------------------------
// <copyright file="ToolAuthorizerDifferentialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Search;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The differential proof for authorization PR 6. Each input goes through the
/// old gate and through the authorizer path, each with a new context, and the
/// two decisions must be identical: outcome, reason, advice, consent request
/// with its candidates and options, matched grants, per-candidate coverage,
/// store lookups, and the analysis that the process may execute. The old gate
/// is <c>ShellPolicyCoordinator.EvaluateAsync</c> for a shell call and the old
/// executor gate (kept in the harness) for any other call. The authorizer path
/// is the production executor.
/// </summary>
/// <remarks>
/// The inputs are every catalog case in four states (interactive and unattended,
/// Approval and Auto), the hard-deny parity corpus, a shell corpus in twelve
/// grant and mode states, and the other tool families. When the current gate
/// asks for consent, the test also compares the retry with a "Once" answer.
/// </remarks>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ToolAuthorizerDifferentialTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Verbs that the corpus grant states store, so that coverage and the
    // grant-versus-trusted-root order both get exercised.
    private static readonly string[] GrantedVerbs =
    [
        "cd", "cat", "ls", "grep", "sed", "head", "wc", "rm", "find", "make",
        "git status", "git log", "git fetch", "git push", "gh api", "python3", "inspect"
    ];

    public static IEnumerable<TheoryDataRow<string, bool, bool>> States()
        => from grants in new[] { "none", "anywhere", "external" }
           from interactive in new[] { true, false }
           from auto in new[] { false, true }
           select new TheoryDataRow<string, bool, bool>(grants, interactive, auto);

    public static IEnumerable<TheoryDataRow<TrustAudience, bool, string>> ToolStates()
        => from audience in new[] { TrustAudience.Personal, TrustAudience.Team, TrustAudience.Public }
           from interactive in new[] { true, false }
           from mode in new[] { "default", "Approval", "Auto", "Deny" }
           select new TheoryDataRow<TrustAudience, bool, string>(audience, interactive, mode);

    [SlopwatchSuppress("SW001", "Bash catalog rows require a POSIX filesystem, as in the disposition matrix.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Bash matrix rows require POSIX filesystem semantics.")]
    [MemberData(nameof(ShellApprovalCases.BashRows), MemberType = typeof(ShellApprovalCases))]
    public Task Bash_catalog_case_gets_the_same_decision(string caseId)
        => AssertCatalogCaseAsync(ShellApprovalCases.Get(caseId));

    [Theory]
    [MemberData(nameof(ShellApprovalCases.PowerShellRows), MemberType = typeof(ShellApprovalCases))]
    public Task Power_shell_catalog_case_gets_the_same_decision(string caseId)
        => AssertCatalogCaseAsync(ShellApprovalCases.Get(caseId));

    [SlopwatchSuppress("SW001", "The Bash corpus uses POSIX paths and links.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash corpus requires POSIX filesystem semantics.")]
    [MemberData(nameof(States))]
    public Task Bash_corpus_gets_the_same_decision(string grants, bool interactive, bool auto)
        => AssertCorpusAsync(ShellApprovalHost.Bash, BashCorpus(), grants, interactive, auto);

    [Theory]
    [MemberData(nameof(States))]
    public Task Power_shell_corpus_gets_the_same_decision(string grants, bool interactive, bool auto)
        => AssertCorpusAsync(ShellApprovalHost.PowerShell7, PowerShellCorpus(), grants, interactive, auto);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Hard_deny_corpus_gets_the_same_decision(bool interactive, bool auto)
    {
        var rows = HardDenyParityCorpusTests.Rows
            .Where(row => IsPosix || row.Host is not (ShellApprovalHost.Bash or ShellApprovalHost.Bash52))
            .ToList();
        var differences = new List<string>();
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(row => (row.Host, row.Policy, row.Audience)))
        {
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "differential-deny",
                new ShellApprovalInvocation("true", Audience: group.Key.Audience, Interactive: interactive, Host: group.Key.Host),
                Approvals.None,
                fixture.ActorSystem,
                Ct,
                shellApprovalMode: auto ? ToolApprovalMode.Auto : null,
                policy: group.Key.Policy);
            foreach (var row in group)
            {
                var comparisons = await harness.CompareWithOnceRetryAsync(
                    ShellTool.ToolName,
                    ShellArguments(row.Command, harness.ProjectDirectory),
                    Ct);
                Collect(differences, row.Id, comparisons, outcomes);
            }
        }

        Report($"hard deny | interactive={interactive} auto={auto}", rows.Count, outcomes);
        AssertNoDifferences(differences, rows.Count);
    }

    [Theory]
    [MemberData(nameof(ToolStates))]
    public async Task Other_tools_get_the_same_decision(TrustAudience audience, bool interactive, string mode)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "differential-tools",
            new ShellApprovalInvocation("true", Audience: audience, Interactive: interactive, Host: NativeHost),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            policy: new ShellApprovalHarnessPolicy { ConfigureTools = config => Configure(config, mode) });
        var root = Path.GetDirectoryName(harness.ProjectDirectory)!;
        var external = Path.Combine(root, "workspaces", "external");
        var mcpTools = RegisterOtherToolFamilies(harness);

        var differences = new List<string>();
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var inputs = ToolCorpus(harness.ProjectDirectory, harness.SessionDirectory, external, harness.Paths, mcpTools);
        foreach (var (id, toolName, arguments) in inputs)
        {
            var comparisons = await harness.CompareWithOnceRetryAsync(toolName, arguments, Ct);
            Collect(differences, id, comparisons, outcomes);

            // A chat grant for the exact request must cover the call on both paths.
            if (comparisons[0].Request is not null)
            {
                await harness.RecordChatGrantAsync(comparisons[0], Ct);
                Collect(differences, $"{id}+chat-grant", await harness.CompareWithOnceRetryAsync(toolName, arguments, Ct), outcomes);
            }
        }

        Report($"tools | {audience} interactive={interactive} mode={mode}", inputs.Count, outcomes);
        AssertNoDifferences(differences, inputs.Count);
    }

    // The one decision shape that the union refuses: a consent request that also
    // carries advice. The policy builds that pair for a file write to the platform
    // temporary directory. Both paths must turn it into advice, and the union must
    // fail loudly if the pair ever reaches it unchanged.
    [Fact]
    public async Task Consent_request_with_advice_becomes_advice_on_both_paths()
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "differential-advice",
            new ShellApprovalInvocation("true", Host: NativeHost),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            policy: new ShellApprovalHarnessPolicy { ConfigureTools = config => Configure(config, "Approval") });
        var arguments = ToolInput.Create(
            "Path", Path.Combine(Path.GetTempPath(), "netclaw-differential-out.txt"),
            "Content", "x");

        var pair = harness.BuildNonShellConsentRequest(FileWriteTool.ToolName, arguments);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, pair.Outcome);
        Assert.IsType<ToolCorrection.ManagedTemporaryDirectorySuggested>(pair.AgentCorrection);
        Assert.Throws<InvalidOperationException>(() => AuthorizationDecision.From(pair, analysis: null));

        var comparison = await harness.CompareToolAsync(FileWriteTool.ToolName, arguments, Ct);
        Assert.True(comparison.IsIdentical, Describe(FileWriteTool.ToolName, comparison));
        Assert.Equal("outcome=RequiresAgentCorrection", comparison.Outcome);
        Assert.Contains("corrections=temporary:", comparison.Current, StringComparison.Ordinal);
    }

    private static ShellApprovalHost NativeHost
        => OperatingSystem.IsWindows() ? ShellApprovalHost.PowerShell7 : ShellApprovalHost.Bash;

    private async Task AssertCatalogCaseAsync(ShellApprovalCase testCase)
    {
        var differences = new List<string>();
        foreach (var interactive in new[] { true, false })
        {
            foreach (var auto in new[] { false, true })
            {
                await using var harness = await ShellApprovalHarness.CreateAsync(
                    testCase.Id,
                    testCase.Invocation with { Interactive = interactive },
                    testCase.Approvals,
                    fixture.ActorSystem,
                    Ct,
                    shellApprovalMode: auto ? ToolApprovalMode.Auto : null);
                var comparison = await harness.CompareAsync(Ct);
                if (!comparison.IsIdentical)
                    differences.Add(Describe($"{testCase.Id} interactive={interactive} auto={auto}", comparison));
            }
        }

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    private async Task AssertCorpusAsync(
        ShellApprovalHost host,
        IReadOnlyList<string> commands,
        string grants,
        bool interactive,
        bool auto)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "differential-corpus",
            new ShellApprovalInvocation("true", Interactive: interactive, Host: host),
            grants switch
            {
                "none" => Approvals.None,
                "anywhere" => Approvals.PersistentAnywhere(GrantedVerbs),
                "external" => Approvals.PersistentHere(ApprovalDirectoryShape.External, GrantedVerbs),
                _ => throw new ArgumentOutOfRangeException(nameof(grants), grants, "Unknown grant state.")
            },
            fixture.ActorSystem,
            Ct,
            shellApprovalMode: auto ? ToolApprovalMode.Auto : null);
        var root = Path.GetDirectoryName(harness.ProjectDirectory)!;
        var external = Path.Combine(root, "workspaces", "external");
        harness.CreateProjectDirectory("sub");

        var differences = new List<string>();
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            var resolved = command
                .Replace("{P}", harness.ProjectDirectory, StringComparison.Ordinal)
                .Replace("{X}", external, StringComparison.Ordinal)
                .Replace("{S}", harness.SessionDirectory, StringComparison.Ordinal)
                .Replace("{C}", harness.Paths.ConfigDirectory, StringComparison.Ordinal);
            var comparisons = await harness.CompareWithOnceRetryAsync(
                ShellTool.ToolName,
                ShellArguments(resolved, harness.ProjectDirectory),
                Ct);
            Collect(differences, command, comparisons, outcomes);
        }

        Report($"{host} | grants={grants} interactive={interactive} auto={auto}", commands.Count, outcomes);
        AssertNoDifferences(differences, commands.Count);
    }

    private static IDictionary<string, object?> ShellArguments(string command, string workingDirectory)
        => ToolInput.Create("Command", command, "WorkingDirectory", workingDirectory);

    private static void Collect(
        List<string> differences,
        string id,
        IReadOnlyList<AuthorizationComparison> comparisons,
        SortedDictionary<string, int> outcomes)
    {
        for (var index = 0; index < comparisons.Count; index++)
        {
            var comparison = comparisons[index];
            outcomes[comparison.Outcome] = outcomes.GetValueOrDefault(comparison.Outcome) + 1;
            if (!comparison.IsIdentical)
                differences.Add(Describe(index == 0 ? id : $"{id} (retry after Once)", comparison));
        }
    }

    // One line per test in the test output, so a run can report its coverage.
    private static void Report(string state, int inputs, SortedDictionary<string, int> outcomes)
        => TestContext.Current.TestOutputHelper?.WriteLine(
            $"differential | {state} | inputs={inputs} comparisons={outcomes.Values.Sum()} | " +
            string.Join(", ", outcomes.Select(pair => $"{pair.Key}:{pair.Value}")));

    private static void AssertNoDifferences(List<string> differences, int inputs)
        => Assert.True(
            differences.Count == 0,
            $"{differences.Count} differences in {inputs} inputs:{Environment.NewLine}" +
            string.Join(Environment.NewLine, differences.Take(20)));

    private static string Describe(string id, AuthorizationComparison comparison)
        => $"DIFFERENCE {id}{Environment.NewLine}--- current{Environment.NewLine}{comparison.Current}" +
           $"{Environment.NewLine}--- authorizer{Environment.NewLine}{comparison.Authorizer}";

    private static void Configure(ToolConfig config, string mode)
    {
        foreach (var profile in new[] { config.AudienceProfiles.Personal, config.AudienceProfiles.Team, config.AudienceProfiles.Public })
        {
            // One allowed and one refused server, so that both MCP admission rules run.
            profile.McpServersMode = ToolProfileMode.Allowlist;
            profile.AllowedMcpServers = ["memorizer"];
            profile.McpServerToolGrants = new() { ["memorizer"] = ["search_memories"] };
            if (mode == "default")
                continue;

            var approvalMode = Enum.Parse<ToolApprovalMode>(mode);
            profile.ApprovalPolicy ??= new ToolApprovalConfig();
            foreach (var tool in OtherToolNames)
                profile.ApprovalPolicy.ToolOverrides[tool] = approvalMode;
        }
    }

    private static readonly string[] OtherToolNames =
    [
        FileReadTool.ToolName, FileListTool.ToolName, FileSearchTool.ToolName, FileWriteTool.ToolName,
        FileEditTool.ToolName, AttachFileTool.ToolName, SetWorkingDirectoryTool.ToolName, ToolOutputReadTool.ToolName,
        "web_fetch", "web_search", "search_tools", "load_tool", "skill_load", "skill_read_resource", "skill_manage",
        "set_reminder", "cancel_reminder", "list_reminders", "get_reminder_history", CheckBackgroundJobTool.ToolName,
        "set_webhook", "delete_webhook", "memorizer/search_memories", "memorizer/get", "other-server/run"
    ];

    // Registers the tool families that the daemon adds after its first-party tools.
    // Authorization reads only the registered name, grant category, and type, so
    // the actor references are never used.
    private static (string Allowed, string ToolRefused, string ServerRefused) RegisterOtherToolFamilies(ShellApprovalHarness harness)
    {
        harness.RegisterTools((registry, policy) =>
        {
            registry.Register(new WebSearchTool(new EmptySearchBackend()));
            registry.WithReminderTools(ActorRefs.Nobody, TimeProvider.System, new SchedulingConfig());
            registry.WithWebhookRouteTools(ActorRefs.Nobody);
            registry.WithBackgroundJobTools(ActorRefs.Nobody);
            var skillRegistry = new SkillRegistry();
            var refresher = new SkillInventoryRefresher(
                harness.Paths,
                new SkillFeedsConfig(),
                [],
                skillRegistry,
                new SkillIndexPublisher(skillRegistry, new SkillIndexContextLayer(), static (_, _) => true));
            registry.WithSkillTools(
                policy,
                skillRegistry,
                harness.Paths,
                new NoOpSkillContentScanner(),
                new UnavailablePromptLoader(),
                refresher,
                NullLogger<FileReadTool>.Instance);
        });
        return (
            harness.RegisterMcpTool("memorizer", "search_memories"),
            harness.RegisterMcpTool("memorizer", "get"),
            harness.RegisterMcpTool("other-server", "run"));
    }

    private static List<(string Id, string ToolName, IDictionary<string, object?> Arguments)> ToolCorpus(
        string project,
        string session,
        string external,
        NetclawPaths paths,
        (string Allowed, string ToolRefused, string ServerRefused) mcp)
    {
        var configFile = Path.Combine(paths.ConfigDirectory, "netclaw.json");
        var platformTemporary = Path.Combine(Path.GetTempPath(), "netclaw-differential-out.txt");
        var inputs = new List<(string, string, IDictionary<string, object?>)>();
        void Add(string id, string tool, params object?[] pairs)
        {
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < pairs.Length; index += 2)
                arguments[(string)pairs[index]!] = pairs[index + 1];
            inputs.Add((id, tool, arguments));
        }

        foreach (var (name, path) in new[]
                 {
                     ("project", Path.Combine(project, "notes.txt")),
                     ("session", Path.Combine(session, "notes.txt")),
                     ("external", Path.Combine(external, "secret.txt")),
                     ("config", configFile),
                     ("relative", "notes.txt"),
                     ("tilde", "~/notes.txt"),
                     ("temporary", platformTemporary),
                     ("parent", Path.Combine(project, "..", "escape.txt")),
                 })
        {
            Add($"file_read {name}", FileReadTool.ToolName, "Path", path);
            Add($"file_write {name}", FileWriteTool.ToolName, "Path", path, "Content", "x");
            Add($"file_edit {name}", FileEditTool.ToolName, "Path", path, "OldString", "a", "NewString", "b");
            Add($"attach_file {name}", AttachFileTool.ToolName, "Path", path);
        }

        foreach (var (name, directory) in new[] { ("project", project), ("external", external), ("config", paths.ConfigDirectory) })
        {
            Add($"file_list {name}", FileListTool.ToolName, "Path", directory);
            Add($"file_search {name}", FileSearchTool.ToolName, "Root", directory, "Pattern", "*.md");
            Add($"set_working_directory {name}", SetWorkingDirectoryTool.ToolName, "Path", directory);
        }

        Add("file_read without path", FileReadTool.ToolName);
        Add("tool_output_read", ToolOutputReadTool.ToolName, "CallId", "call-1");
        Add("web_fetch", "web_fetch", "Url", "https://example.com/page");
        Add("web_search", "web_search", "Query", "netclaw");
        Add("search_tools", "search_tools", "Query", "file");
        Add("load_tool", "load_tool", "Name", "web_fetch");
        Add("skill_load", "skill_load", "Name", "missing-skill");
        Add("skill_read_resource", "skill_read_resource", "Name", "missing-skill", "Path", "README.md");
        Add("skill_manage", "skill_manage", "Action", "write", "Name", "new-skill", "Content", "x");
        Add("set_reminder", "set_reminder", "Prompt", "check", "In", "5m");
        Add("cancel_reminder", "cancel_reminder", "Id", "r1");
        Add("list_reminders", "list_reminders");
        Add("get_reminder_history", "get_reminder_history", "Id", "r1");
        Add("set_webhook", "set_webhook", "Name", "hook");
        Add("delete_webhook", "delete_webhook", "Name", "hook");
        Add("check_background_job", CheckBackgroundJobTool.ToolName, "JobId", "job-1");
        Add("mcp allowed", mcp.Allowed, "query", "x");
        Add("mcp allowed with metadata", mcp.Allowed, "query", "x", "_rationale", "Look it up.");
        Add("mcp tool refused", mcp.ToolRefused, "id", "1");
        Add("mcp server refused", mcp.ServerRefused);
        Add("unknown tool", "no_such_tool");
        return inputs;
    }

    private static IReadOnlyList<string> BashCorpus()
        => ShellApprovalCases.All
            .Where(testCase => testCase.Invocation.Host is ShellApprovalHost.Bash or ShellApprovalHost.Bash52)
            .Select(testCase => testCase.Invocation.Command)
            .Concat(HardDenyParityCorpusTests.Rows
                .Where(row => row.Host is ShellApprovalHost.Bash or ShellApprovalHost.Bash52 && row.Policy is null)
                .Select(row => row.Command))
            .Concat(BashForms)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> PowerShellCorpus()
        => ShellApprovalCases.All
            .Where(testCase => testCase.Invocation.Host is ShellApprovalHost.PowerShell7 or ShellApprovalHost.WindowsPowerShell51)
            .Select(testCase => testCase.Invocation.Command)
            .Concat(HardDenyParityCorpusTests.Rows
                .Where(row => row.Host is ShellApprovalHost.PowerShell7 or ShellApprovalHost.WindowsPowerShell51 && row.Policy is null)
                .Select(row => row.Command))
            .Concat(PowerShellForms)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // Forms that the catalog does not state with real paths: the project ({P}),
    // an external directory ({X}), the session ({S}), and the config directory ({C}).
    private static readonly string[] BashForms =
    [
        "cat {P}/notes.txt", "cat {X}/secret.txt", "cat {S}/notes.txt", "cat {C}/netclaw.json",
        "ls {X}", "rm -rf {X}/build", "rm {P}/notes.txt", "echo hi > {X}/out.txt", "echo hi > {P}/out.txt",
        "cd {X} && cat notes.txt", "cd {X} && inspect; cat *.md", "cd {P}/sub && git status; ls",
        "cd {X} && make && git status", "cd sub && cat result.txt | sed -n '1p'; ls .",
        "git -C {X} status", "find {X} -name '*.md'", "find . -name '*.md' -exec cat {} +",
        "grep -r TODO {P}", "head -n 5 {X}/secret.txt | wc -l", "python3 {X}/script.py",
        "cat /tmp/netclaw-out.txt", "echo hi > /tmp/netclaw-out.txt", "cat $HOME/.ssh/id_rsa",
        "cat ~/.netclaw/config/netclaw.json", "echo ok; cat {X}/secret.txt", "for f in *.md; do cat \"$f\"; done",
        "X=1 git push", "GIT_SSH_COMMAND=evil bash -lc \"git push\"", "bash -lc \"cat {X}/secret.txt\"",
        "git status && gh api repos/x/y", "echo hello", "true", ":", "printf ok", "cat",
    ];

    private static readonly string[] PowerShellForms =
    [
        "Get-Content {P}/notes.txt", "Get-Content {X}/secret.txt", "Set-Location {X}; Get-ChildItem",
        "Remove-Item {X}/build -Recurse", "Get-ChildItem -Recurse {P}", "Write-Output ok",
        "git status", "git push", "Get-Date", "Start-Process pwsh -Verb RunAs",
    ];

    private sealed class EmptySearchBackend : ISearchBackend
    {
        public Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)
            => throw new InvalidOperationException("Authorization tests do not run a search.");
    }

    private sealed class UnavailablePromptLoader : IMcpPromptSkillLoader
    {
        public ValueTask<McpPromptSkillLoadResult> LoadAsync(
            McpPromptSkillSource source,
            IReadOnlyDictionary<string, string>? arguments,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(McpPromptSkillLoadResult.Failed("unavailable"));
    }
}
