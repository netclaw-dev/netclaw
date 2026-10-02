// -----------------------------------------------------------------------
// <copyright file="AuthorizationCorpusProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Search;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Netclaw.Tools.Authorization.Consent;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The corpus probe of <c>scripts/authorization-corpus/run.py</c>. The script
/// copies this file and one adapter into a disposable worktree of the revision
/// under test. This file is not part of the repository build.
/// </summary>
/// <remarks>
/// The probe writes one line for each decision: the state, the input index, and
/// the canonical text of the decision. The script runs the probe on two
/// revisions and compares the lines. The probe replaces every run-specific path
/// with a placeholder, so equal decisions give equal lines.
/// </remarks>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class AuthorizationCorpusProbe(ShellApprovalMatrixFixture fixture)
{
    // The verbs that the grant states store, so that coverage and the
    // grant-versus-trusted-root order both get exercised.
    private static readonly string[] GrantedVerbs =
    [
        "cd", "cat", "ls", "grep", "sed", "head", "wc", "rm", "find", "make",
        "git status", "git log", "git fetch", "git push", "gh api", "python3", "inspect"
    ];

    // The harness gives each PowerShell state a fake Windows root with a new GUID.
    // Policy text shows it with either separator.
    private static readonly Regex WindowsHarnessRoot = new(
        @"C:([/\\])netclaw-approval-matrix\1[0-9a-f]{32}",
        RegexOptions.CultureInvariant);

    [Fact]
    public async Task Run()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Required("NETCLAW_CORPUS_IN");
        var output = Required("NETCLAW_CORPUS_OUT");
        var repository = Required("NETCLAW_CORPUS_REPOSITORY");
        var selected = Environment.GetEnvironmentVariable("NETCLAW_CORPUS_STATES") is { Length: > 0 } filter
            ? filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : null;
        var commands = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(input, ct))
            ?? throw new InvalidOperationException("The corpus file is empty.");

        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var cleaner = new Cleaner(temporaryRoot, Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository)));

        await using var writer = new StreamWriter(output, append: false, new UTF8Encoding(false));
        await writer.WriteLineAsync($"#adapter\t{CorpusProbeAdapter.Name}");
        foreach (var state in ShellStates().Where(state => selected?.Contains(state.Id) != false))
            await RunShellStateAsync(writer, state, commands, temporaryRoot, cleaner, ct);
        foreach (var state in ToolStates().Where(state => selected?.Contains(state.Id) != false))
            await RunToolStateAsync(writer, state, cleaner, ct);
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The corpus probe needs the environment variable {name}.");

    private sealed record ShellState(string Id, ShellApprovalHost Host, string Grants, bool Interactive, bool Auto);

    private sealed record ToolState(string Id, TrustAudience Audience, bool Interactive, string Mode);

    // Bash states need POSIX filesystem semantics, as the disposition matrix does.
    private static IEnumerable<ShellState> ShellStates()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var grants in new[] { "none", "anywhere", "external" })
            foreach (var interactive in new[] { true, false })
            foreach (var auto in new[] { false, true })
                yield return new($"bash-{grants}-{(interactive ? "i" : "u")}-{(auto ? "auto" : "approval")}", ShellApprovalHost.Bash, grants, interactive, auto);
        }

        foreach (var grants in new[] { "none", "anywhere" })
        foreach (var auto in new[] { false, true })
            yield return new($"pwsh-{grants}-i-{(auto ? "auto" : "approval")}", ShellApprovalHost.PowerShell7, grants, true, auto);
    }

    private static IEnumerable<ToolState> ToolStates()
        => from audience in new[] { TrustAudience.Personal, TrustAudience.Team, TrustAudience.Public }
           from interactive in new[] { true, false }
           from mode in new[] { "default", "Approval", "Auto", "Deny" }
           select new ToolState($"tools-{audience}-{(interactive ? "i" : "u")}-{mode}", audience, interactive, mode);

    private async Task RunShellStateAsync(
        StreamWriter writer,
        ShellState state,
        IReadOnlyList<string> commands,
        string temporaryRoot,
        Cleaner cleaner,
        CancellationToken ct)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "corpus",
            new ShellApprovalInvocation("true", Interactive: state.Interactive, Host: state.Host),
            state.Grants switch
            {
                "none" => Approvals.None,
                "anywhere" => Approvals.PersistentAnywhere(GrantedVerbs),
                "external" => Approvals.PersistentHere(ApprovalDirectoryShape.External, GrantedVerbs),
                _ => throw new ArgumentOutOfRangeException(nameof(state), state.Grants, "Unknown grant state.")
            },
            fixture.ActorSystem,
            ct,
            shellApprovalMode: state.Auto ? ToolApprovalMode.Auto : null);
        harness.CreateProjectDirectory("sub");
        var root = Path.GetDirectoryName(harness.ProjectDirectory)!;
        var external = Path.Combine(root, "workspaces", "external");
        var executor = PrivateField<DispatchingToolExecutor>(harness, "_executor");
        var scope = new Scope(harness, root, cleaner);

        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index]
                .Replace("{P}", harness.ProjectDirectory, StringComparison.Ordinal)
                .Replace("{X}", external, StringComparison.Ordinal)
                .Replace("{S}", harness.SessionDirectory, StringComparison.Ordinal)
                .Replace("{C}", harness.Paths.ConfigDirectory, StringComparison.Ordinal)
                .Replace("{T}", temporaryRoot, StringComparison.Ordinal);
            var call = new FunctionCallContent("corpus", ShellTool.ToolName, ToolInput.Create(
                "Command", command, "WorkingDirectory", harness.ProjectDirectory));
            await WriteWithOnceRetryAsync(writer, state.Id, index.ToString(CultureInfo.InvariantCulture), scope, executor, call,
                TrustAudience.Personal, state.Interactive, ct);
        }
    }

    private async Task RunToolStateAsync(StreamWriter writer, ToolState state, Cleaner cleaner, CancellationToken ct)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "corpus-tools",
            new ShellApprovalInvocation("true", Audience: state.Audience, Interactive: state.Interactive, Host: NativeHost),
            Approvals.None,
            fixture.ActorSystem,
            ct,
            policy: new ShellApprovalHarnessPolicy { ConfigureTools = config => Configure(config, state.Mode) });
        var root = Path.GetDirectoryName(harness.ProjectDirectory)!;
        var external = Path.Combine(root, "workspaces", "external");
        var executor = PrivateField<DispatchingToolExecutor>(harness, "_executor");
        var mcp = RegisterOtherToolFamilies(harness);
        var scope = new Scope(harness, root, cleaner);

        var inputs = ToolCorpus(harness.ProjectDirectory, harness.SessionDirectory, external, harness.Paths, mcp);
        for (var index = 0; index < inputs.Count; index++)
        {
            var (_, toolName, arguments) = inputs[index];
            var call = new FunctionCallContent("corpus", toolName, arguments);
            var id = index.ToString(CultureInfo.InvariantCulture);
            var request = await WriteWithOnceRetryAsync(writer, state.Id, id, scope, executor, call, state.Audience, state.Interactive, ct);

            // A chat grant for the exact request must cover the call.
            if (request is not null)
            {
                await RecordChatGrantAsync(harness, state.Audience, request, ct);
                await WriteWithOnceRetryAsync(writer, state.Id, $"{id}+chat-grant", scope, executor, call, state.Audience, state.Interactive, ct);
            }
        }
    }

    private sealed record Scope(ShellApprovalHarness Harness, string Root, Cleaner Cleaner);

    // Writes the decision, and when it asks for consent, the retry with a "Once" answer.
    private static async Task<ToolApprovalContext?> WriteWithOnceRetryAsync(
        StreamWriter writer,
        string state,
        string id,
        Scope scope,
        DispatchingToolExecutor executor,
        FunctionCallContent call,
        TrustAudience audience,
        bool interactive,
        CancellationToken ct)
    {
        var first = await DecideAsync(scope, executor, call, CreateContext(scope.Harness, audience, interactive, oneTime: null), ct);
        await writer.WriteAsync($"{state}\t{id}\t{first.Text}\n");
        if (first.Request is not { } request)
            return null;

        var consent = OneTimeApprovalKeys.CreateConsent(call.Name, request);
        var retry = await DecideAsync(scope, executor, call, CreateContext(scope.Harness, audience, interactive, consent), ct);
        await writer.WriteAsync($"{state}\t{id}+once\t{retry.Text}\n");
        return request;
    }

    private static async Task<(string Text, ToolApprovalContext? Request)> DecideAsync(
        Scope scope,
        DispatchingToolExecutor executor,
        FunctionCallContent call,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var before = scope.Harness.ApprovalService.CheckCount;
        var fields = await CorpusProbeAdapter.DecideAsync(executor, call, context, ct);
        var checks = scope.Harness.ApprovalService.CheckCount - before;
        return (scope.Cleaner.Clean(Render(fields, checks), scope.Harness, scope.Root), fields.Request);
    }

    private static ToolExecutionContext CreateContext(
        ShellApprovalHarness harness,
        TrustAudience audience,
        bool interactive,
        OneTimeConsent? oneTime)
    {
        var context = TestToolExecutionContext.CreateBound(
            "signalr/approval-matrix",
            harness.SessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                ProjectDirectory = harness.ProjectDirectory,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(interactive)
            });
        if (oneTime is not null)
            context.Approval.SeedOneTimeConsent(oneTime);
        return context;
    }

    private static Task RecordChatGrantAsync(
        ShellApprovalHarness harness,
        TrustAudience audience,
        ToolApprovalContext request,
        CancellationToken ct)
    {
        var candidates = request.Candidates is { Count: > 0 } requestCandidates
            ? requestCandidates
            : request.CandidateVerbs.Select(verb => new ApprovalCandidate(verb, Directory: null)).ToList();
        return harness.ApprovalService.RecordApprovalCandidatesAsync(
            (ToolApprovalSessionId)"signalr/approval-matrix",
            audience,
            new ToolName(request.ToolName),
            candidates.Select(candidate => new ToolApprovalGrant(candidate, GrantScope.Session.Instance)).ToList(),
            ct);
    }

    private static T PrivateField<T>(ShellApprovalHarness harness, string name)
        => (T)(typeof(ShellApprovalHarness)
                   .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                   ?.GetValue(harness)
               ?? throw new InvalidOperationException($"The harness has no field {name}."));

    private static ShellApprovalHost NativeHost
        => OperatingSystem.IsWindows() ? ShellApprovalHost.PowerShell7 : ShellApprovalHost.Bash;

    /// <summary>The canonical text of one decision. The adapter supplies the fields.</summary>
    internal static string Render(CorpusDecisionFields fields, int checks)
    {
        var lines = new List<string>
        {
            $"outcome={fields.Outcome}",
            $"allow={fields.AllowReason}",
            $"deny={fields.DenyReason}",
            $"deny-message={fields.DenyMessage}",
            $"corrections={string.Join(";", fields.Corrections?.Select(Describe) ?? [])}",
            $"matches={string.Join(";", fields.Matches.Select(match => $"{match.Scope}:{match.Pattern}"))}",
            $"checks={checks}",
            $"execution={fields.Execution}",
        };
        if (fields.Request is { } request)
        {
            lines.Add($"request.tool={request.ToolName}");
            lines.Add($"request.display={request.DisplayText}");
            lines.Add($"request.patterns={string.Join(";", request.Patterns)}");
            lines.Add($"request.verbs={string.Join(";", request.CandidateVerbs)}");
            lines.Add($"request.options={string.Join(";", request.Options.Select(option => $"{option.Key.Value}={option.Label}"))}");
            lines.Add($"request.cwd={request.Cwd}");
            lines.Add($"request.messy={request.IsMessy}");
            lines.Add($"request.candidates={string.Join(";", request.Candidates?.Select(Describe) ?? ["<null>"])}");
            lines.Add($"request.retry={request.IsManagedTemporaryRetry}:{request.ManagedTemporaryDirectory}:{request.PlatformTemporaryRoot}");
            lines.Add($"request.repository={request.RepositoryCommonDirectory}");
        }

        // The grant timestamp changes with each run. Only its presence is compared.
        lines.AddRange(fields.Trace.Rows.Select(row => string.Join(
            '|',
            "trace",
            row.Stage,
            row.CandidateId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.ExecutableBasename ?? string.Empty,
            row.Outcome,
            row.Reason,
            row.Coverage?.ToString() ?? string.Empty,
            row.ScopeRelation,
            row.GrantTimestamp is null ? string.Empty : "timestamp")));
        return string.Join('\n', lines);
    }

    private static string Describe(ApprovalCandidate candidate)
        => string.Join(
            '|',
            candidate.Verb,
            candidate.Directory ?? "<cwd>",
            candidate.Shell?.ToString() ?? "<none>",
            candidate.VerbTokens is null ? "<none>" : string.Join(' ', candidate.VerbTokens),
            candidate.AssignmentDigest?.Value ?? "<none>");

    private static string Describe(ToolCorrection correction)
        => correction switch
        {
            ToolCorrection.ManagedTemporaryDirectorySuggested temporary => $"temporary:{temporary.Target}",
            ToolCorrection.NativeToolSuggested native => $"native:{native.ToolName.Value}",
            ToolCorrection.ProjectDirectorySuggested project => $"project:{project.Directory}",
            ToolCorrection.ShellWorkingDirectorySuggested directory => $"directory:{directory.Directory}",
            // A correction kind that one revision lacks prints its record text.
            _ => correction.ToString()
        };

    /// <summary>Replaces each run-specific path with a placeholder, longest path first.</summary>
    private sealed class Cleaner(string temporaryRoot, string repository)
    {
        public string Clean(string text, ShellApprovalHarness harness, string root)
        {
            var external = Path.Combine(root, "workspaces", "external");
            var replacements = new[]
                {
                    (harness.ProjectDirectory, "{P}"),
                    (harness.SessionDirectory, "{S}"),
                    (external, "{X}"),
                    (root, "{R}"),
                    (temporaryRoot, "{T}"),
                    (repository, "{REPOSITORY}"),
                }
                .Where(pair => pair.Item1.Length > 0)
                .OrderByDescending(pair => pair.Item1.Length);
            foreach (var (path, placeholder) in replacements)
                text = text.Replace(path, placeholder, StringComparison.Ordinal);

            return WindowsHarnessRoot.Replace(text, "C:$1{W}")
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
        }
    }

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
        var registry = PrivateField<ToolRegistry>(harness, "_registry");
        var policy = PrivateField<ServiceProvider>(harness, "_services").GetRequiredService<ToolAccessPolicy>();
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
        var platformTemporary = Path.Combine(Path.GetTempPath(), "netclaw-corpus-out.txt");
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

    private sealed class EmptySearchBackend : ISearchBackend
    {
        public Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)
            => throw new InvalidOperationException("The corpus probe does not run a search.");
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

/// <summary>The fields of one decision, in the shape that both decision types can supply.</summary>
internal sealed record CorpusDecisionFields(
    string Outcome,
    string? AllowReason,
    string? DenyReason,
    string? DenyMessage,
    IReadOnlyList<ToolCorrection>? Corrections,
    ToolApprovalContext? Request,
    IReadOnlyList<ToolApprovalMatch> Matches,
    ShellPolicyDecisionTrace Trace,
    string Execution);
