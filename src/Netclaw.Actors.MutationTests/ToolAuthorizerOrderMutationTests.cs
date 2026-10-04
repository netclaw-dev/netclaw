// -----------------------------------------------------------------------
// <copyright file="ToolAuthorizerOrderMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Proves the rule order of <see cref="ToolAuthorizer"/>. A covering grant exists
/// in every case, so a rule that moves behind the grant rule, or a grant rule
/// that moves ahead of an earlier rule, changes a decision.
/// </summary>
public sealed class ToolAuthorizerOrderMutationTests : IDisposable
{
    // A temporary root without links, so that the macOS /var alias cannot change a path decision.
    private readonly NetclawPaths _paths = new(Path.Combine(
        CanonicalTemporaryDirectory(), "netclaw-authorizer-order-mutations", Guid.NewGuid().ToString("N")));
    private readonly SessionStoragePaths _storage;
    private readonly string _outsideDirectory;

    public ToolAuthorizerOrderMutationTests()
    {
        _paths.EnsureDirectoriesExist();
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")));
        Directory.CreateDirectory(_storage.SessionDirectory.Value);
        _outsideDirectory = Path.Combine(_paths.BasePath, "outside");
        Directory.CreateDirectory(_outsideDirectory);
    }

    // Hard deny precedes every later rule. A grant for the denied phrase must not allow it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hard_deny_precedes_a_covering_grant(bool interactive)
    {
        var grants = (await PromptVerbsAsync("git push")).Append("git fetch").ToArray();
        var authorizer = CreateAuthorizer(grants, hardDenyPatterns: ["git fetch"]);

        var permitted = await AuthorizeAsync(authorizer, "git push", TrustAudience.Personal, interactive);
        var allowed = Assert.IsType<AuthorizationDecision.Allowed>(permitted);
        Assert.Equal(ToolAllowReason.StoredApproval, allowed.Reason);

        var forbidden = await AuthorizeAsync(authorizer, "git fetch", TrustAudience.Personal, interactive);
        var denied = Assert.IsType<AuthorizationDecision.Denied>(forbidden);
        Assert.Equal("hard_deny_custom_deny", denied.Reason);
    }

    // An admission denial is final. No later rule can clear it.
    [Fact]
    public async Task Audience_denial_precedes_every_later_rule()
    {
        var authorizer = CreateAuthorizer(await PromptVerbsAsync("git status"), hardDenyPatterns: []);

        var decision = await AuthorizeAsync(authorizer, "git status", TrustAudience.Team, interactive: true);

        var denied = Assert.IsType<AuthorizationDecision.Denied>(decision);
        Assert.Equal("tool_not_allowed_for_audience_profile", denied.Reason);
    }

    // D2: an attended and an unattended call get the same rules. With the
    // default Personal profile, a stored grant covers a path outside the
    // project in both, with the ordinary stored-grant reason.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_grant_decides_a_readable_path_outside_the_project(bool interactive)
    {
        var command = ReadCommand(Path.Combine(_outsideDirectory, "secret.txt"));
        var logger = new AuthorizationReasonLogger();
        var executor = CreateExecutor(await PromptVerbsAsync(command), hardDenyPatterns: [], logger: logger);

        var decision = await executor.EvaluateAuthorizationAsync(
            ShellCall(command, workingDirectory: null),
            CreateContext(TrustAudience.Personal, interactive),
            CancellationToken.None);

        AssertStoredGrantAllow(decision, logger);
    }

    // The trusted-root rule precedes the covering grant. A bounded profile
    // confines attended and unattended calls alike, and a grant cannot open a
    // working directory outside its roots.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trusted_root_denial_precedes_a_covering_grant(bool interactive)
    {
        var authorizer = CreateAuthorizer(
            await PromptVerbsAsync("git status", _outsideDirectory),
            hardDenyPatterns: [],
            boundedWrites: true);

        var decision = await AuthorizeAsync(
            authorizer, "git status", TrustAudience.Personal, interactive, _outsideDirectory);

        var denied = Assert.IsType<AuthorizationDecision.Denied>(decision);
        Assert.Equal("shell_working_directory_outside_trust_zone", denied.Reason);
    }

    // D2: the one difference. Without a covering grant, a call that would
    // prompt in a chat is denied in an unattended run, because nobody can answer.
    [Fact]
    public async Task An_unattended_call_without_a_grant_is_denied()
    {
        var command = ReadCommand(Path.Combine(_outsideDirectory, "secret.txt"));
        var authorizer = CreateAuthorizer([], hardDenyPatterns: []);

        var attended = await AuthorizeAsync(authorizer, command, TrustAudience.Personal, interactive: true);
        var unattended = await AuthorizeAsync(authorizer, command, TrustAudience.Personal, interactive: false);

        Assert.IsType<AuthorizationDecision.NeedsConsent>(attended);
        var denied = Assert.IsType<AuthorizationDecision.Denied>(unattended);
        Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, denied.Reason);
        Assert.Contains("nobody can answer a prompt", denied.Message, StringComparison.Ordinal);
    }

    // Negative control: Auto mode never reads grants, so the trusted-root rule still decides.
    [Fact]
    public async Task Auto_mode_keeps_the_trusted_root_denial()
    {
        var command = ReadCommand(Path.Combine(_outsideDirectory, "secret.txt"));
        var authorizer = CreateAuthorizer(
            await PromptVerbsAsync(command), hardDenyPatterns: [], ToolApprovalMode.Auto, boundedWrites: true);

        var decision = await AuthorizeAsync(authorizer, command, TrustAudience.Personal, interactive: false);

        var denied = Assert.IsType<AuthorizationDecision.Denied>(decision);
        Assert.Equal("shell_path_outside_trust_zone", denied.Reason);
        Assert.Null(denied.Message);
    }

    // Negative control: a grant never opens a protected path. The control plane stays closed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_grant_never_opens_a_protected_path(bool interactive)
    {
        // The grant names the same verb. The read verb takes its phrase from an unprotected file.
        var grants = await PromptVerbsAsync(ReadCommand(Path.Combine(_outsideDirectory, "secret.txt")));
        var command = ReadCommand(Path.Combine(_paths.ConfigDirectory, "netclaw.json"));
        var authorizer = CreateAuthorizer(grants, hardDenyPatterns: []);

        var decision = await AuthorizeAsync(authorizer, command, TrustAudience.Personal, interactive);

        var denied = Assert.IsType<AuthorizationDecision.Denied>(decision);
        Assert.Null(denied.Message);
    }

    // The same rule for the working directory: a folder grant decides, attended or not (D2).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_grant_decides_for_a_working_directory_outside_the_project(bool interactive)
    {
        var logger = new AuthorizationReasonLogger();
        var executor = CreateExecutor(
            await PromptVerbsAsync("git status", _outsideDirectory),
            hardDenyPatterns: [],
            logger: logger);

        var decision = await executor.EvaluateAuthorizationAsync(
            ShellCall("git status", _outsideDirectory),
            CreateContext(TrustAudience.Personal, interactive),
            CancellationToken.None);

        AssertStoredGrantAllow(decision, logger);
    }

    private static void AssertStoredGrantAllow(
        AuthorizationDecision decision,
        AuthorizationReasonLogger logger)
    {
        var allowed = Assert.IsType<AuthorizationDecision.Allowed>(decision);
        Assert.NotEmpty(allowed.Matches);
        Assert.Equal(ToolAllowReason.StoredApproval, allowed.Reason);
        var completion = allowed.Trace.Rows[^1];
        Assert.Equal(ShellPolicyTraceStage.Completion, completion.Stage);
        Assert.Equal(ShellPolicyTraceOutcome.Allow, completion.Outcome);
        Assert.Equal(ShellPolicyTraceReason.AllCandidatesCovered, completion.Reason);
        Assert.Equal(ToolAllowReason.StoredApproval.ToString(), Assert.Single(logger.AuthorizationReasons));
    }

    // #2306: a bare glob gives Unknown command words, so no grant can cover the
    // call. It gets a rewrite correction, not a prompt, and it does not run.
    // A mutant that drops the correction turns it back into a prompt or a denial.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_command_words_get_a_rewrite_correction(bool interactive)
    {
        var command = OperatingSystem.IsWindows() ? "Remove-Item *.tmp" : "rm *.tmp";

        var decision = await AuthorizeAsync(
            CreateAuthorizer([], hardDenyPatterns: []),
            command,
            TrustAudience.Personal,
            interactive);

        var correction = Assert.IsType<AuthorizationDecision.CorrectionRequired>(decision);
        Assert.Contains(
            correction.Corrections.Items,
            static item => item is ToolCorrection.ShellCommandWordsRewriteSuggested
            {
                Rewrite: ShellCommandWordsRewrite.UsePathGlob
            });
    }

    private static string ReadCommand(string path)
        => OperatingSystem.IsWindows() ? $"Get-Content '{path}'" : $"cat '{path}'";

    public void Dispose() => Directory.Delete(_paths.BasePath, recursive: true);

    // The phrases of the interactive prompt for a command, so that each grant
    // covers exactly what the host shell asks for.
    private async Task<IReadOnlyList<string>> PromptVerbsAsync(string command, string? workingDirectory = null)
    {
        var decision = await AuthorizeAsync(
            CreateAuthorizer([], hardDenyPatterns: []),
            command,
            TrustAudience.Personal,
            interactive: true,
            workingDirectory);
        var consent = Assert.IsType<AuthorizationDecision.NeedsConsent>(decision);
        Assert.NotEmpty(consent.Request.CandidateVerbs);
        return consent.Request.CandidateVerbs;
    }

    private ToolAuthorizer CreateAuthorizer(
        IReadOnlyList<string> grantedVerbs,
        IReadOnlyList<string> hardDenyPatterns,
        ToolApprovalMode shellMode = ToolApprovalMode.Approval,
        bool boundedWrites = false)
        => CreateExecutor(grantedVerbs, hardDenyPatterns, shellMode, logger: null, boundedWrites).Authorizer;

    private DispatchingToolExecutor CreateExecutor(
        IReadOnlyList<string> grantedVerbs,
        IReadOnlyList<string> hardDenyPatterns,
        ToolApprovalMode shellMode = ToolApprovalMode.Approval,
        ILogger<DispatchingToolExecutor>? logger = null,
        bool boundedWrites = false)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        if (boundedWrites)
        {
            // A bounded Personal write profile: the trusted-root rule confines
            // every shell call to the session directory, attended or not.
            config.AudienceProfiles.Personal.WriteFiles = new ToolFilesystemAccessProfile
            {
                Mode = ToolFilesystemMode.Roots,
                Roots = [ToolAudienceProfileDefaults.SessionDirectoryToken]
            };
        }

        foreach (var profile in new[]
                 { config.AudienceProfiles.Public, config.AudienceProfiles.Team, config.AudienceProfiles.Personal })
        {
            profile.ApprovalPolicy = new ToolApprovalConfig
            {
                DefaultMode = ToolApprovalMode.Approval,
                ToolOverrides = new() { [ShellTool.ToolName] = shellMode }
            };
        }

        var policy = new ToolAccessPolicy(
            _paths,
            config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(NativeEnvironment, [.. hardDenyPatterns]),
            // The control plane is protected, as in the daemon.
            new ToolPathPolicy(NativeEnvironment, [_paths.ConfigDirectory]));
        var registry = new ToolRegistry();
        registry.Register(new ShellProbeTool());
        return new DispatchingToolExecutor(registry, policy, new VerbGrantService(grantedVerbs), logger);
    }

    private Task<AuthorizationDecision> AuthorizeAsync(
        ToolAuthorizer authorizer,
        string command,
        TrustAudience audience,
        bool interactive,
        string? workingDirectory = null)
        => authorizer.AuthorizeAsync(
            ShellCall(command, workingDirectory),
            CreateContext(audience, interactive),
            CancellationToken.None);

    private static FunctionCallContent ShellCall(string command, string? workingDirectory)
        => new(
            "order-call",
            ShellTool.ToolName,
            workingDirectory is null
                ? new Dictionary<string, object?> { ["Command"] = command }
                : new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = workingDirectory });

    private ToolExecutionContext CreateContext(TrustAudience audience, bool interactive)
        => new(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound("signalr/order-mutation", _storage),
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = interactive
                    ? new InteractiveApprovalCapability.Available(new UnexpectedApprovalBridge())
                    : new InteractiveApprovalCapability.Unavailable()
            },
            ToolExecutionTimeout.Default);

    // Keeps the reason field of each "Tool authorization evaluated" line.
    private sealed class AuthorizationReasonLogger : ILogger<DispatchingToolExecutor>
    {
        internal List<string?> AuthorizationReasons { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields
                || !formatter(state, exception).StartsWith("Tool authorization evaluated", StringComparison.Ordinal))
            {
                return;
            }

            AuthorizationReasons.Add(fields
                .Where(static field => field.Key == "AuthorizationReason")
                .Select(static field => field.Value?.ToString())
                .SingleOrDefault());
        }
    }

    // A grant store with one chat grant for each named phrase.
    private sealed class VerbGrantService(IReadOnlyCollection<string> verbs) : IToolApprovalService, IShellApprovalMatchService
    {
        public Task<ShellApprovalMatchResult> MatchShellCandidatesAsync(
            ShellApprovalMatchRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(ShellApprovalMatchResult.Create(
                request.Candidates,
                persistentStoreFailure: null,
                request.Candidates
                    .Select(candidate => verbs.Contains(candidate.Candidate.Verb, StringComparer.Ordinal)
                        ? ShellGrantCandidateResult.Session(candidate)
                        : ShellGrantCandidateResult.Uncovered(candidate))
                    .ToArray()));

        public Task<ToolApprovalCheckResult> CheckApprovalAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("A shell call uses the per-candidate match.");

        public Task RecordApprovalCandidatesAsync(
            ToolApprovalSessionId sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ToolApprovalGrant> grants,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The order tests do not record grants.");
    }

    // The authorizer reads only the name, grant category, and type of the tool.
    private sealed class ShellProbeTool : INetclawTool
    {
        private readonly AIFunction _function = AIFunctionFactory.Create((string Command) => Command, ShellTool.ToolName);
        public string Name => ShellTool.ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Never runs.";
        public string GrantCategory => "shell";
        public System.Text.Json.JsonElement ParameterSchema => _function.JsonSchema;
        public AITool ToAITool() => _function;

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct)
            => throw new InvalidOperationException("The authorizer must not run the tool.");
    }

    // The host shell, as the daemon resolves it: PowerShell on Windows, Bash elsewhere.
    // The parameterless policy constructors always use Linux Bash. On Windows that
    // parses a Windows path as a relative POSIX path, which no trusted root can judge.
    private static readonly ShellExecutionEnvironment NativeEnvironment = OperatingSystem.IsWindows()
        ? ShellExecutionEnvironment.CreatePowerShell(@"C:\Program Files\PowerShell\7\pwsh.exe", PwshDialect.PowerShell7)
        : ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);

    private static string CanonicalTemporaryDirectory()
    {
        var fullPath = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The temporary directory has no path root.");
        foreach (var segment in fullPath[current.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            current = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }

        return current;
    }

    private sealed class UnexpectedApprovalBridge : IParentConsentBridge
    {
        public Task<ConsentStep> RequestConsentAsync(ParentApprovalRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("The authorizer must not request consent.");
    }
}
