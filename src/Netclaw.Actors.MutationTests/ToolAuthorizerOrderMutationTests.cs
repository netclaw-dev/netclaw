// -----------------------------------------------------------------------
// <copyright file="ToolAuthorizerOrderMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
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

    // Today the trusted-root check precedes the covering grant. An unattended
    // call outside every trusted root stays denied, although a grant covers it.
    // The interactive control proves that the same grant covers the same call.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trusted_root_precedes_a_covering_grant(bool interactive)
    {
        var command = OperatingSystem.IsWindows()
            ? $"Get-Content '{Path.Combine(_outsideDirectory, "secret.txt")}'"
            : $"cat '{Path.Combine(_outsideDirectory, "secret.txt")}'";
        var authorizer = CreateAuthorizer(await PromptVerbsAsync(command), hardDenyPatterns: []);

        var decision = await AuthorizeAsync(authorizer, command, TrustAudience.Personal, interactive);

        if (interactive)
        {
            var allowed = Assert.IsType<AuthorizationDecision.Allowed>(decision);
            Assert.Equal(ToolAllowReason.StoredApproval, allowed.Reason);
        }
        else
        {
            var denied = Assert.IsType<AuthorizationDecision.Denied>(decision);
            Assert.Equal("shell_path_outside_trust_zone", denied.Reason);
        }
    }

    // The same order for the working directory. The later projected path check
    // covers path operands, so only this case separates the trusted-root rule.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trusted_root_of_the_working_directory_precedes_a_covering_grant(bool interactive)
    {
        var authorizer = CreateAuthorizer(
            await PromptVerbsAsync("git status", _outsideDirectory),
            hardDenyPatterns: []);

        var decision = await AuthorizeAsync(authorizer, "git status", TrustAudience.Personal, interactive, _outsideDirectory);

        if (interactive)
        {
            var allowed = Assert.IsType<AuthorizationDecision.Allowed>(decision);
            Assert.Equal(ToolAllowReason.StoredApproval, allowed.Reason);
        }
        else
        {
            var denied = Assert.IsType<AuthorizationDecision.Denied>(decision);
            Assert.Equal("shell_working_directory_outside_trust_zone", denied.Reason);
        }
    }

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

    private ToolAuthorizer CreateAuthorizer(IReadOnlyList<string> grantedVerbs, IReadOnlyList<string> hardDenyPatterns)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        foreach (var profile in new[]
                 { config.AudienceProfiles.Public, config.AudienceProfiles.Team, config.AudienceProfiles.Personal })
        {
            profile.ApprovalPolicy = new ToolApprovalConfig
            {
                DefaultMode = ToolApprovalMode.Approval,
                ToolOverrides = new() { [ShellTool.ToolName] = ToolApprovalMode.Approval }
            };
        }

        var policy = new ToolAccessPolicy(
            _paths,
            config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(NativeEnvironment, [.. hardDenyPatterns]),
            new ToolPathPolicy(NativeEnvironment, []));
        var registry = new ToolRegistry();
        registry.Register(new ShellProbeTool());
        var executor = new DispatchingToolExecutor(registry, policy, new VerbGrantService(grantedVerbs));
        return executor.Authorizer;
    }

    private Task<AuthorizationDecision> AuthorizeAsync(
        ToolAuthorizer authorizer,
        string command,
        TrustAudience audience,
        bool interactive,
        string? workingDirectory = null)
        => authorizer.AuthorizeAsync(
            new FunctionCallContent(
                "order-call",
                ShellTool.ToolName,
                workingDirectory is null
                    ? new Dictionary<string, object?> { ["Command"] = command }
                    : new Dictionary<string, object?> { ["Command"] = command, ["WorkingDirectory"] = workingDirectory }),
            new ToolExecutionContext(
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
                ToolExecutionTimeout.Default),
            CancellationToken.None);

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
        public Task<ConsentAnswer> RequestConsentAsync(ParentApprovalRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("The authorizer must not request consent.");
    }
}
