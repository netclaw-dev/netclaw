// -----------------------------------------------------------------------
// <copyright file="ToolAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Applies the configured tool, path, shell, and approval policies to one tool invocation.
/// </summary>
public sealed class ToolAccessPolicy
{
    private enum ApprovalOptionProfile
    {
        OneShotOnly,
        Standard,
        StandardWithDirectory,
        McpTool
    }

    private static readonly IReadOnlyList<ToolApprovalOption> ManagedTemporaryRetryOptions =
        Array.AsReadOnly<ToolApprovalOption>(
        [
            new(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ]);

    private readonly ToolConfig _toolConfig;
    private readonly EffectivePolicyDefaults _defaults;
    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly ShellCommandPolicy _shellCommandPolicy;
    private readonly ToolPathPolicy _toolPathPolicy;
    private readonly ShellApprovalMatcher _shellApprovalMatcher;
    private readonly PathAccessPolicy _pathAccessPolicy;
    private readonly IToolApprovalMatcher _fileApprovalMatcher;
    private readonly FeatureGates _featureGates;
    private readonly ReviewedSafeShellPolicy? _safeVerbPolicy;
    private readonly TemporaryPathCorrectionPolicy _temporaryPathCorrectionPolicy;

    internal ApprovalShell Shell => _shellCommandPolicy.Environment.Grammar == ShellGrammar.Bash
        ? ApprovalShell.Bash
        : ApprovalShell.PowerShell;

    internal ShellExecutionEnvironment ShellEnvironment => _shellCommandPolicy.Environment;

    internal ToolConfig ToolConfig => _toolConfig;

    internal ToolPathPolicy ProtectedPathPolicy => _toolPathPolicy;

    internal ShellCommandPolicy ShellCommandPolicy => _shellCommandPolicy;

    internal PathAccessPolicy SharedPathAccessPolicy => _pathAccessPolicy;

    internal bool IsEligiblePlatformTemporaryPath(string path)
        => _temporaryPathCorrectionPolicy.IsEligiblePlatformTemporaryPath(path);

    public ToolAccessPolicy(
        NetclawPaths paths,
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy shellCommandPolicy,
        ToolPathPolicy toolPathPolicy,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        FeatureGates? featureGates = null,
        SafeVerbList? safeVerbs = null)
        : this(
            paths,
            toolConfig,
            defaults,
            shellCommandPolicy,
            toolPathPolicy,
            TemporaryPathCorrectionPolicy.Create(shellCommandPolicy.Environment),
            fileApprovalMatcher,
            featureGates,
            safeVerbs)
    {
    }

    internal ToolAccessPolicy(
        NetclawPaths paths,
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy shellCommandPolicy,
        ToolPathPolicy toolPathPolicy,
        TemporaryPathCorrectionPolicy platformTemporaryScopePolicy,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        FeatureGates? featureGates = null,
        SafeVerbList? safeVerbs = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // shellCommandPolicy (deny-list) and toolPathPolicy (protected paths) are
        // required security controls — non-nullable so a caller cannot omit them.
        // The shell gate below dereferences them directly, so a stray null fails
        // loudly at the point of use rather than silently skipping a check.
        _toolConfig = toolConfig;
        _defaults = defaults;
        _profileResolver = new ToolAudienceProfileResolver(toolConfig);
        _shellCommandPolicy = shellCommandPolicy;
        _toolPathPolicy = toolPathPolicy;
        if (!ReferenceEquals(shellCommandPolicy.Environment, toolPathPolicy.Environment))
        {
            throw new ArgumentException(
                "Shell command and path policies must use the same shell environment.",
                nameof(toolPathPolicy));
        }

        _shellApprovalMatcher = new ShellApprovalMatcher(shellCommandPolicy.Environment);
        _pathAccessPolicy = new PathAccessPolicy(
            toolConfig,
            paths,
            toolPathPolicy);
        _fileApprovalMatcher = fileApprovalMatcher ?? DefaultApprovalMatcher.Instance;
        _featureGates = featureGates ?? FeatureGates.AllEnabled;
        _safeVerbPolicy = safeVerbs is null
            ? null
            : new ReviewedSafeShellPolicy(safeVerbs, _pathAccessPolicy);
        _temporaryPathCorrectionPolicy = platformTemporaryScopePolicy;
    }

    public IReadOnlyList<AITool> FilterExposedTools(
        IEnumerable<AITool> tools,
        ToolRegistry registry,
        EffectiveTrustContext? trustContext)
        => tools
            .Where(tool =>
            {
                var name = GetToolName(tool);
                if (name is null)
                    return true;

                var registration = registry.GetRegistrationByToolName(name);
                return registration is null || IsToolExposed(registration, trustContext);
            })
            .ToList();

    public IReadOnlyList<INetclawTool> FilterDiscoverableTools(
        IEnumerable<INetclawTool> tools,
        ToolInvocationContext context)
        => tools.Where(tool => IsToolExposed(tool, context)).ToList();

    public bool IsToolExposed(ToolRegistration registration, EffectiveTrustContext? trustContext)
        => IsToolExposed(registration.Tool, ResolveAudience(trustContext));

    public bool IsToolExposed(INetclawTool tool, ToolInvocationContext context)
        => IsToolExposed(tool, ResolveAudience(context));

    public bool IsMcpServerExposed(McpServerName serverName, TrustAudience audience)
        => _profileResolver.IsMcpServerAllowed(serverName, audience);

    internal bool IsToolExposed(INetclawTool tool, TrustAudience audience)
    {
        // Feature-disabled tools are hidden for ALL audiences
        if (IsFeatureDisabledTool(tool.Name))
            return false;

        if (tool is McpToolAdapter mcp)
            return _profileResolver.IsMcpServerAllowed(new McpServerName(mcp.ServerName), audience)
                && _profileResolver.IsMcpToolAllowed(
                    new McpServerName(mcp.ServerName),
                    new ToolName(mcp.BareToolName),
                    audience)
                && _profileResolver.ResolveProfile(audience).ApprovalPolicy?.GetEffectiveMode(mcp.Name)
                    != ToolApprovalMode.Deny;

        if (!_profileResolver.IsToolAllowed(new ToolName(tool.Name), audience))
            return false;

        if (_profileResolver.ResolveProfile(audience).ApprovalPolicy?.GetEffectiveMode(tool.Name)
            == ToolApprovalMode.Deny)
        {
            return false;
        }

        if (IsShellCoupledTool(tool))
            return ResolveShellMode() == ShellExecutionMode.HostAllowed && audience == TrustAudience.Personal;

        return true;
    }

    public ToolAuthorizationDecision AuthorizeInvocation(INetclawTool tool, ToolExecutionContext context)
        => AuthorizeInvocation(tool, context, arguments: null);

    public ToolAuthorizationDecision AuthorizeInvocation(
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments)
        => AuthorizeInvocationCore(
            tool,
            context,
            arguments,
            deferReviewedSafeCoverage: false,
            out _);

    internal ShellPolicyPreflightResult AuthorizeShellPreflight(
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments)
    {
        var decision = AuthorizeInvocationCore(
            tool,
            context,
            arguments,
            deferReviewedSafeCoverage: true,
            out var analysis);

        if (!decision.NeedsApproval)
        {
            return new ShellPolicyPreflightResult.Complete(
                decision,
                decision.Allowed ? analysis : null);
        }

        if (analysis is null)
        {
            return new ShellPolicyPreflightResult.Complete(
                decision,
                authorizedAnalysis: null);
        }

        return decision.ApprovalContext is { } approvalContext
            ? new ShellPolicyPreflightResult.Continue(
                analysis,
                approvalContext,
                ShellEnvironment,
                decision.AgentCorrection)
            : new ShellPolicyPreflightResult.Complete(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                authorizedAnalysis: null);
    }

    private ToolAuthorizationDecision AuthorizeInvocationCore(
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        bool deferReviewedSafeCoverage,
        out ShellCommandAnalysis? authorizedAnalysis)
    {
        authorizedAnalysis = null;

        if (tool is McpToolAdapter mcp)
            return AuthorizeMcpInvocation(mcp, context, arguments);

        var toolName = new ToolName(tool.Name);
        if (!_profileResolver.IsToolAllowed(toolName, context.Invocation))
            return ToolAuthorizationDecision.Deny("tool_not_allowed_for_audience_profile");

        return IsShellCoupledTool(tool)
            ? AuthorizeShellInvocation(
                tool,
                toolName,
                context,
                arguments,
                deferReviewedSafeCoverage,
                out authorizedAnalysis)
            : AuthorizeStructuredInvocation(tool, toolName, context, arguments);
    }

    private ToolAuthorizationDecision AuthorizeMcpInvocation(
        McpToolAdapter tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments)
    {
        var serverName = new McpServerName(tool.ServerName);
        if (!_profileResolver.IsMcpServerAllowed(serverName, context.Invocation))
            return ToolAuthorizationDecision.Deny("mcp_server_not_allowed_for_audience_profile");

        if (!_profileResolver.IsMcpToolAllowed(
                serverName,
                new ToolName(tool.BareToolName),
                context.Invocation))
        {
            return ToolAuthorizationDecision.Deny("mcp_tool_not_allowed_for_audience_profile");
        }

        var toolName = new ToolName(tool.Name);
        var (_, approvalArguments) = ToolCallMeta.ExtractFrom(
            arguments,
            key => ToolArgumentValidator.ResolveMetaField(tool, key));
        var approvalMode = GetApprovalMode(
            toolName,
            context,
            approvalArguments,
            McpApprovalMatcher.Instance);
        return CheckApprovalGate(
            toolName,
            context,
            approvalArguments,
            McpApprovalMatcher.Instance,
            approvalMode);
    }

    private ToolAuthorizationDecision AuthorizeStructuredInvocation(
        INetclawTool tool,
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments)
    {
        var pathDenial = PreflightStructuredPathAccess(tool, context.Invocation, arguments);
        if (pathDenial is not null)
            return pathDenial;

        var matcher = SelectMatcherForTool(toolName);
        var approvalMode = GetApprovalMode(toolName, context, arguments, matcher);
        if (approvalMode == ToolApprovalMode.Deny)
            return ToolAuthorizationDecision.Deny("tool_denied_by_approval_policy");

        return CheckApprovalGate(toolName, context, arguments, matcher, approvalMode);
    }

    private ToolAuthorizationDecision AuthorizeShellInvocation(
        INetclawTool tool,
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        bool deferReviewedSafeCoverage,
        out ShellCommandAnalysis? authorizedAnalysis)
    {
        authorizedAnalysis = null;

        var shellMode = ResolveShellMode();
        if (shellMode == ShellExecutionMode.Off)
            return ToolAuthorizationDecision.Deny("shell_disabled");

        if (shellMode == ShellExecutionMode.SandboxOnly)
            return ToolAuthorizationDecision.Deny("shell_requires_sandbox_backend");

        var shellAudience = ResolveAudience(context.Invocation);
        if (shellAudience != TrustAudience.Personal)
            return ToolAuthorizationDecision.Deny("shell_requires_personal_context");

        // shell_execute authorizes the process before the job starts. This tool
        // can only control a job with the same session, audience, and boundary.
        // It does not create a new shell invocation or require another approval.
        if (string.Equals(tool.Name, CheckBackgroundJobTool.ToolName, StringComparison.Ordinal))
            return ToolAuthorizationDecision.Allow(ToolAllowReason.BackgroundJobLifecycle);

        var shellCommand = ExtractShellCommand(arguments);
        var workingDirectory = context.ResolveShellCwd(ExtractWorkingDirectory(arguments));
        ShellCommandAnalysis? shellAnalysis = null;
        if (shellCommand is not null)
        {
            shellAnalysis = _shellCommandPolicy.Analyze(shellCommand, workingDirectory);
            var hardDenyDecision = _shellCommandPolicy.Evaluate(shellAnalysis);
            if (!hardDenyDecision.Allowed)
                return ToolAuthorizationDecision.Deny(
                    $"hard_deny_{hardDenyDecision.DenyCategory?.ToWireName() ?? "unknown"}");

            if (_toolPathPolicy.CommandReferencesDeniedPath(shellAnalysis))
                return ToolAuthorizationDecision.Deny("shell_references_protected_path");
        }

        // All shell policy checks use the directory that ShellTool executes.
        // The explicit tool argument can be absent while the context supplies
        // an active project, session, or inherited directory.
        var analysisArguments = WithResolvedShellWorkingDirectory(arguments, workingDirectory);
        var shellApproval = shellAnalysis is null
            ? null
            : _shellApprovalMatcher.AnalyzeInvocation(
                toolName,
                analysisArguments,
                shellAnalysis);

        // Shell does not classify an executable as a reader or writer. Once
        // shell capability and command policy pass, every known path must pass
        // the conservative Write file-protection layer.
        if (shellCommand is not null)
        {
            var pathAccessDeny = EnforceShellFileProtection(
                shellApproval!,
                shellAnalysis!,
                workingDirectory,
                context);
            if (pathAccessDeny is not null)
                return pathAccessDeny;
        }

        var mode = GetApprovalMode(toolName, context, arguments, _shellApprovalMatcher);
        var approvalModeDecision = GetApprovalModeDecision(mode);
        if (approvalModeDecision is { Allowed: false })
            return approvalModeDecision;

        authorizedAnalysis = shellAnalysis;

        if (approvalModeDecision is not null)
            return approvalModeDecision;

        return CheckApprovalGate(
            toolName,
            context,
            arguments,
            _shellApprovalMatcher,
            mode,
            shellApproval,
            shellAnalysis,
            deferReviewedSafeCoverage);
    }

    internal bool IsReviewedSafeCandidate(
        ShellPolicyCandidate candidate,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
        => _safeVerbPolicy is not null
           && _safeVerbPolicy.ShortCircuits(
               candidate,
               pathFacts,
               context);

    internal bool IsReviewedSafeIntentCandidate(
        ShellPolicyCandidate candidate,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
        => _safeVerbPolicy is not null
           && _safeVerbPolicy.ShortCircuitsCausalIntent(
               candidate,
               pathFacts,
               context);

    internal bool CausalIntentReferencesProtectedPath(
        ShellPolicyCandidatePathFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Intent?.ResolutionBase is not { } intent
            || string.IsNullOrWhiteSpace(intent.AuthoredValue)
            || facts.Fallbacks.Count == 0)
        {
            return true;
        }

        if (ScopeReferencesProtectedPath(intent)
            || facts.Fallbacks.Any(fallback =>
                ScopeReferencesProtectedPath(fallback.ResolutionBase)))
        {
            return true;
        }

        if (facts.Intent is { } intentPaths
            && ViewReferencesProtectedPath(intentPaths))
        {
            return true;
        }

        return facts.Fallbacks.Any(ViewReferencesProtectedPath);
    }

    private bool ScopeReferencesProtectedPath(ShellPolicyScopePathFact scope)
        => scope is
        {
            State: ShellPolicyPathResolutionState.Known,
            Path: { } path
        }
           && _toolPathPolicy.IsShellDeniedProjectedPath(path);

    private bool ViewReferencesProtectedPath(ShellPolicyResolvedPathView view)
        => view.Facts.Any(fact =>
            fact.Source.Origin is ShellPolicyPathOrigin.EffectiveArgument
                or ShellPolicyPathOrigin.AuthoredArgument
                or ShellPolicyPathOrigin.Redirect
            && fact.Source.Domain is ShellValueDomain.Exact or ShellValueDomain.FiniteSet
            && (fact.State == ShellPolicyPathResolutionState.InvalidKnownValue
                || fact.Paths.Any(path =>
                    _toolPathPolicy.IsShellDeniedProjectedPath(path))));

    internal bool IsCausalIntentDirectoryEligible(string intentDirectory)
    {
        if (ShellEnvironment.Grammar != ShellGrammar.Bash
            || !ShellPathRules.TryNormalize(
                intentDirectory,
                ShellEnvironment.PathStyle,
                out var normalized)
            || !ShellPathRules.Equals(
                normalized,
                intentDirectory,
                ShellEnvironment.PathStyle))
        {
            return false;
        }

        if (_temporaryPathCorrectionPolicy.IsEligiblePlatformTemporaryPath(normalized))
            return true;

        try
        {
            return !PathUtility.ContainsSymlinkSegment("/", normalized);
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or NotSupportedException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal bool AreCausalIntentDirectoriesEligible(
        string intentDirectory,
        IReadOnlyList<string> fallbackDirectories)
        => fallbackDirectories.Count > 0
           && IsCausalIntentDirectoryEligible(intentDirectory)
           && fallbackDirectories.All(IsCausalIntentDirectoryEligible);

    internal ShellApprovalMatcher ShellApprovalMatcher => _shellApprovalMatcher;

    /// <summary>
    /// Applies the file-protection layer after shell capability and command
    /// policy pass. Every known shell path uses conservative <see
    /// cref="PathAccessPolicy.FileOperation.Write"/> authority because Netclaw
    /// does not infer whether an arbitrary executable only reads a path.
    /// </summary>
    private ToolAuthorizationDecision? EnforceShellFileProtection(
        ShellApprovalAnalysis approval,
        ShellCommandAnalysis analysis,
        string? workingDirectory,
        ToolExecutionContext context)
    {
        // Unattended runs cannot send unresolved path syntax to a user. An
        // interactive run keeps the existing one-shot approval path for that
        // syntax. Known paths still pass Write protection below.
        if (approval.IsMessy
            && context.RunScope.InteractiveApproval
            is InteractiveApprovalCapability.Unavailable)
        {
            return ToolAuthorizationDecision.Deny("shell_unresolved_trust_zone_input");
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var expandedWorkingDirectory = PathUtility.ExpandAndNormalize(workingDirectory, workingDirectory: null);
            if (expandedWorkingDirectory is null)
                return ToolAuthorizationDecision.Deny("shell_invalid_working_directory");

            if (!_pathAccessPolicy.Evaluate(
                    expandedWorkingDirectory,
                    context.Invocation,
                    PathAccessPolicy.FileOperation.Write).Allowed)
                return ToolAuthorizationDecision.Deny("shell_working_directory_outside_trust_zone");
        }

        return EnforceKnownShellPaths(
            ShellPolicyPathFacts.CreateExecutionViews(analysis)
                .SelectMany(EnumerateKnownShellPaths),
            context.Invocation);
    }

    /// <summary>
    /// Applies conservative write protection to all paths in a shell policy projection.
    /// </summary>
    /// <remarks>
    /// Causal Bash analysis can add intent and fallback views after shell preflight.
    /// The coordinator must call this method before it checks stored grants or reviewed-safe coverage.
    /// </remarks>
    internal ToolAuthorizationDecision? EnforceProjectedShellFileProtection(
        IReadOnlyList<ShellPolicyCandidatePathFacts> pathFacts,
        ToolInvocationContext context)
        => EnforceKnownShellPaths(
            pathFacts.SelectMany(EnumerateKnownShellPaths),
            context);

    private ToolAuthorizationDecision? EnforceKnownShellPaths(
        IEnumerable<CanonicalShellPath> paths,
        ToolInvocationContext context)
    {
        foreach (var path in paths
                     .Where(static path => !IsNullDevice(path))
                     .DistinctBy(static path => (path.PathStyle, path.Value)))
        {
            if (!_pathAccessPolicy.EvaluateShellPath(path, context).Allowed)
                return ToolAuthorizationDecision.Deny("shell_path_outside_trust_zone");
        }

        return null;
    }

    private static IEnumerable<CanonicalShellPath> EnumerateKnownShellPaths(
        ShellPolicyCandidatePathFacts candidate)
    {
        if (candidate.RealScope.Path is { } realScope)
            yield return realScope;

        foreach (var path in EnumerateKnownShellPaths(candidate.Real))
            yield return path;

        if (candidate.Intent is { } intent)
        {
            foreach (var path in EnumerateKnownShellPaths(intent))
                yield return path;
        }

        foreach (var fallback in candidate.Fallbacks)
        {
            foreach (var path in EnumerateKnownShellPaths(fallback))
                yield return path;
        }
    }

    private static IEnumerable<CanonicalShellPath> EnumerateKnownShellPaths(
        ShellPolicyResolvedPathView view)
    {
        if (view.ResolutionBase.Path is { } resolutionBase)
            yield return resolutionBase;

        foreach (var path in view.Facts
                     .Where(static fact => fact.State == ShellPolicyPathResolutionState.Known)
                     .SelectMany(static fact => fact.Paths))
        {
            yield return path;
        }
    }

    private static bool IsNullDevice(CanonicalShellPath path)
        => path.PathStyle == ShellPathStyle.Posix
           && string.Equals(path.Value, "/dev/null", StringComparison.Ordinal);

    private ToolAuthorizationDecision? PreflightStructuredPathAccess(
        INetclawTool tool,
        ToolInvocationContext context,
        IDictionary<string, object?>? arguments)
    {
        if (!string.Equals(tool.GrantCategory, "file", StringComparison.Ordinal))
            return null;

        var request = tool.Name switch
        {
            FileReadTool.ToolName or FileListTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.Read),
            FileSearchTool.ToolName =>
                (Argument: "Root", Operation: PathAccessPolicy.FileOperation.Read),
            FileWriteTool.ToolName or FileEditTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.Write),
            AttachFileTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.Attach),
            SetWorkingDirectoryTool.ToolName =>
                (Argument: "Path", Operation: PathAccessPolicy.FileOperation.DeclareProjectScope),
            _ => default
        };

        if (request.Argument is null)
            return ToolAuthorizationDecision.Deny("path_access_descriptor_missing");

        var rawPath = ToolArgumentHelper.GetString(arguments, request.Argument);
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        var decision = _pathAccessPolicy.Evaluate(rawPath, context, request.Operation);
        return !decision.Allowed
               && decision.Failure is PathAccessPolicy.PathAccessFailure.AccessDenied
            ? ToolAuthorizationDecision.Deny("path_access_denied", decision.Error)
            : null;
    }

    private static string? ExtractShellCommand(IDictionary<string, object?>? arguments)
    {
        // Use the shared extractor so JsonElement-valued arguments (the
        // shape LLM-generated tool calls arrive in) get string-converted
        // correctly. The direct `is string` pattern previously here
        // silently returned null for every real shell call, which disabled
        // the hard-deny pre-check at AuthorizeInvocation. The matcher's
        // GetCommand uses ToolArgumentHelper.GetString — mirror it here
        // for consistency.
        if (arguments is null)
            return null;

        return ToolArgumentHelper.GetString(arguments, "Command")
            ?? ToolArgumentHelper.GetString(arguments, "command");
    }

    private static string? ExtractWorkingDirectory(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        return ToolArgumentHelper.GetString(arguments, "WorkingDirectory");
    }

    private static IDictionary<string, object?>? WithResolvedShellWorkingDirectory(
        IDictionary<string, object?>? arguments,
        string? resolvedWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(resolvedWorkingDirectory)
            || !string.IsNullOrWhiteSpace(ExtractWorkingDirectory(arguments)))
        {
            return arguments;
        }

        var analysisArguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
                analysisArguments[key] = value;
        }

        analysisArguments["WorkingDirectory"] = resolvedWorkingDirectory;
        return analysisArguments;
    }

    private ToolAuthorizationDecision CheckApprovalGate(
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        IToolApprovalMatcher matcher,
        ToolApprovalMode mode,
        ShellApprovalAnalysis? shellApproval = null,
        ShellCommandAnalysis? shellAnalysis = null,
        bool deferReviewedSafeCoverage = false)
    {
        var approvalModeDecision = GetApprovalModeDecision(mode);
        if (approvalModeDecision is not null)
            return approvalModeDecision;

        // The approval policy is authoritative for every channel — there is no
        // safe-list auto-grant for non-interactive callers. A non-interactive
        // caller (reminder, webhook, sub-agent without an approval bridge) that
        // hits an approval-gated tool fails closed unless the patterns are
        // already in the persistent approval store.

        // Approval prompts carry three views of the invocation:
        // - `patterns`: the exact blocked units shown to the user and reused by
        //   approve-once retries.
        // - `candidates`: the (verb, directory) pairs evaluated against
        //   persisted ApprovalEntry records by the gate. Candidates include
        //   path operands, redirect targets, and each pipeline clause.
        //   A null directory uses ToolExecutionContext.Cwd.
        // - `candidateVerbs`: the verb-only projection of `candidates`, kept
        //   for renderers (Slack/Discord builders) that bullet-list verbs in
        //   the prompt body. Button labels stay fixed; runtime values like
        //   paths never enter button text because Slack caps button text at
        //   76 chars and Discord at 80.
        // The shell process and the approval parser must use one cwd. The tool
        // argument can omit it because the context supplies the project or
        // session directory. Give that resolved value to the parser too.
        var isShell = string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal);
        var resolvedShellCwd = isShell
            ? context.ResolveShellCwd(ExtractWorkingDirectory(arguments))
            : null;
        if (isShell)
            context.Approval.SetCwd(resolvedShellCwd);

        var analysisArguments = isShell
            ? WithResolvedShellWorkingDirectory(arguments, resolvedShellCwd)
            : arguments;
        var patterns = shellApproval?.Patterns
            ?? matcher.ExtractPatterns(toolName, analysisArguments);
        var candidates = shellApproval?.Candidates
            ?? matcher.ExtractCandidates(toolName, analysisArguments);
        var displayText = shellApproval?.DisplayText
            ?? matcher.FormatForDisplay(toolName, arguments);
        var isMessy = shellApproval?.IsMessy
            ?? matcher.IsMessy(toolName, analysisArguments);

        IReadOnlyList<ApprovalCandidate> approvalCandidates = candidates;
        ToolCorrection? agentCorrection = null;

        if (isShell && shellAnalysis is not null)
        {
            agentCorrection = _temporaryPathCorrectionPolicy.Evaluate(
                shellAnalysis,
                approvalCandidates,
                arguments,
                context.Invocation);
        }
        else if (!isShell)
        {
            agentCorrection = _temporaryPathCorrectionPolicy.EvaluateStructuredFileChange(
                toolName,
                arguments,
                context.Invocation,
                _toolPathPolicy);
        }

        // A clean shell command can combine reviewed-safe candidates with candidates
        // that need a stored grant. Remove only candidates that independently
        // satisfy both the safe-verb and reviewed diagnostic path rules. The approval store
        // must still cover every remaining candidate.
        if (_safeVerbPolicy is not null
            && isShell
            && !isMessy
            && approvalCandidates.Count > 0)
        {
            if (agentCorrection is null
                && !_temporaryPathCorrectionPolicy.IsPlatformTemporaryRoot(context.Approval.Cwd)
                && _safeVerbPolicy.CanShortCircuitAfterProjectDeclaration(
                    approvalCandidates,
                    context.Approval.Cwd,
                    context.Invocation))
            {
                agentCorrection = new ToolCorrection.ProjectDirectorySuggested(context.Approval.Cwd!);
            }

            if (!deferReviewedSafeCoverage)
            {
                approvalCandidates = approvalCandidates
                    .Where(candidate => !_safeVerbPolicy.ShortCircuits(
                        candidate,
                        context.Approval.Cwd,
                        context.Invocation))
                    .ToList();

                if (approvalCandidates.Count == 0)
                    return ToolAuthorizationDecision.Allow(ToolAllowReason.ReviewedSafePolicy);
            }
        }

        var candidateVerbs = approvalCandidates
            .Select(static candidate => candidate.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var managedTemporaryRetry = context.Approval.ManagedTemporaryRetry;
        var isManagedTemporaryRetry = managedTemporaryRetry is not null;
        IReadOnlyList<ToolApprovalOption> options;
        if (isManagedTemporaryRetry)
        {
            options = ManagedTemporaryRetryOptions;
        }
        else
        {
            options = BuildApprovalOptions(GetApprovalOptionProfile(
                toolName,
                isMessy,
                !isShell || approvalCandidates.All(HasReusableShellPhrase),
                matcher is ShellApprovalMatcher
                && IsShellDirectoryApprovalAvailable(
                    approvalCandidates,
                    context.Approval.Cwd,
                    GetSessionOwnedApprovalDirectories(context),
                    ShellEnvironment.PathStyle)));
        }

        var approvalContext = new ToolApprovalContext(
            toolName.Value,
            displayText,
            patterns,
            candidateVerbs,
            options,
            Cwd: context.Approval.Cwd,
            IsMessy: isMessy,
            Candidates: approvalCandidates)
        {
            IsManagedTemporaryRetry = isManagedTemporaryRetry,
            ManagedTemporaryDirectory = managedTemporaryRetry?.ManagedTemporaryDirectory,
            PlatformTemporaryRoot = managedTemporaryRetry?.PlatformTemporaryRoot
        };

        return ToolAuthorizationDecision.RequiresApproval(
            approvalContext,
            isManagedTemporaryRetry ? null : agentCorrection);
    }

    private ToolApprovalMode GetApprovalMode(
        ToolName toolName,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        IToolApprovalMatcher matcher)
    {
        var audience = ResolveAudience(context.Invocation);
        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(_toolConfig.AudienceProfiles, audience);
        var approvalModeKey = matcher.GetApprovalModeKey(toolName, arguments);
        return ResolveApprovalMode(
            profile.ApprovalPolicy,
            approvalModeKey,
            toolName,
            arguments,
            audience,
            matcher);
    }

    private static ToolAuthorizationDecision? GetApprovalModeDecision(ToolApprovalMode mode)
        => mode switch
        {
            ToolApprovalMode.Approval => null,
            ToolApprovalMode.Auto => ToolAuthorizationDecision.Allow(ToolAllowReason.PolicyAuto),
            ToolApprovalMode.Deny => ToolAuthorizationDecision.Deny("tool_denied_by_approval_policy"),
            _ => ToolAuthorizationDecision.Deny("internal_policy_failure")
        };

    internal static ToolApprovalContext NarrowShellApprovalContext(
        ToolApprovalContext context,
        IReadOnlyList<ApprovalCandidate> unapprovedCandidates,
        IReadOnlyCollection<string> sessionOwnedDirectories,
        ShellPathStyle pathStyle)
    {
        var candidateVerbs = unapprovedCandidates
            .Select(static candidate => candidate.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyList<ToolApprovalOption> options;
        if (context.IsManagedTemporaryRetry)
        {
            options = ManagedTemporaryRetryOptions;
        }
        else
        {
            options = BuildApprovalOptions(GetApprovalOptionProfile(
                new ToolName(ShellTool.ToolName),
                isMessy: false,
                unapprovedCandidates.All(HasReusableShellPhrase),
                IsShellDirectoryApprovalAvailable(
                    unapprovedCandidates,
                    context.Cwd,
                    sessionOwnedDirectories,
                    pathStyle)));
        }

        return context with
        {
            Patterns = candidateVerbs,
            CandidateVerbs = candidateVerbs,
            Candidates = unapprovedCandidates,
            Options = options
        };
    }

    /// <summary>
    /// Returns true when every candidate's effective directory is one of the
    /// current session's named storage directories. Persisting an "Always
    /// here" grant scoped to one of those directories is dead-on-arrival because
    /// the next session has different paths. The button is hidden in that case so
    /// operators can pick "This chat" (the equivalent in-session
    /// semantics) or "Always anywhere" (folder-agnostic) instead.
    /// </summary>
    private static bool AllCandidatesResolveToSessionOwnedDirectory(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyCollection<string> sessionOwnedDirectories)
    {
        if (sessionOwnedDirectories.Count == 0 || candidates.Count == 0)
            return false;

        foreach (var candidate in candidates)
        {
            var effective = candidate.Directory ?? cwd;
            if (string.IsNullOrEmpty(effective))
                return false;

            if (!sessionOwnedDirectories.Any(directory =>
                    PathUtility.AreEquivalentPaths(effective, directory)))
                return false;
        }

        return true;
    }

    internal static IReadOnlyCollection<string> GetSessionOwnedApprovalDirectories(
        ToolExecutionContext context)
    {
        if (context.SessionStorage is not { } storage)
            return context.SessionDirectory is { Length: > 0 } sessionDirectory
                ? [sessionDirectory]
                : [];

        return new[]
            {
                storage.SessionDirectory.Value,
                storage.ManagedTemporary.Directory.Value,
                storage.ArtifactDirectory.Value,
                storage.WorktreeDirectory.Value
            }
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsShellDirectoryApprovalAvailable(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyCollection<string> sessionOwnedDirectories,
        ShellPathStyle pathStyle)
    {
        if (IsCwdTooShallow(cwd, pathStyle))
            return false;

        if (AllCandidatesResolveToSessionOwnedDirectory(candidates, cwd, sessionOwnedDirectories))
            return false;

        return true;
    }

    private static ApprovalOptionProfile GetApprovalOptionProfile(
        ToolName toolName,
        bool isMessy,
        bool hasReusablePhraseForEveryCandidate,
        bool includeDirectory)
    {
        if (isMessy || !hasReusablePhraseForEveryCandidate)
            return ApprovalOptionProfile.OneShotOnly;

        if (toolName.IsMcp)
            return ApprovalOptionProfile.McpTool;

        return includeDirectory
            ? ApprovalOptionProfile.StandardWithDirectory
            : ApprovalOptionProfile.Standard;
    }

    /// <summary>
    /// Builds the prompt's button row. The five-button default
    /// (Once / This chat / Always here / Always anywhere / Deny) is pruned
    /// in three cases:
    /// <list type="bullet">
    /// <item><b>Messy commands</b> (bash control-flow / unbalanced
    /// quotes/brackets) — only <c>Once</c> and <c>Deny</c> are offered.
    /// Persistence is impossible because the matcher cannot extract a verb
    /// chain to remember.</item>
    /// <item><b>Shallow cwd</b> (path depth fails the minimum-scope check) —
    /// <c>Always here</c> is omitted so an operator cannot accidentally write
    /// a folder-scoped grant for a too-shallow root like <c>/etc/</c>.
    /// <c>This chat</c> and <c>Always anywhere</c> remain available.</item>
    /// <item><b>Session-owned effective directory</b> (every candidate's
    /// effective directory is one of the current session's named storage
    /// directories) —
    /// <c>Always here</c> is omitted because the saved grant would be scoped
    /// to a directory that won't recur. <c>This chat</c> already provides
    /// the equivalent in-session semantics without polluting the persistent
    /// store.</item>
    /// <item><b>No directory scope</b> (all non-shell tools) — <c>Always
    /// here</c> is omitted because these matchers grant independently of cwd.
    /// For MCP tools, the remaining persistent choice is labeled <c>Always
    /// allow this tool</c> because it persists a canonical-tool grant.</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<ToolApprovalOption> BuildApprovalOptions(ApprovalOptionProfile profile)
    {
        if (profile is ApprovalOptionProfile.OneShotOnly)
        {
            return
            [
                new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ];
        }

        var options = new List<ToolApprovalOption>(5)
        {
            new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolApprovalOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel)
        };

        if (profile is ApprovalOptionProfile.StandardWithDirectory)
        {
            options.Add(new ToolApprovalOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel));
        }

        options.Add(new ToolApprovalOption(
            ApprovalOptionKeys.ApproveEverywhereKey,
            ApprovalOptionKeys.LabelFor(
                ApprovalOptionKeys.ApproveEverywhere,
                profile is ApprovalOptionProfile.McpTool)));
        options.Add(new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel));

        return options;
    }

    private static bool HasReusableShellPhrase(ApprovalCandidate candidate) =>
        candidate.Shell is not null &&
        candidate.VerbTokens is { Count: > 0 } tokens &&
        tokens.All(static token =>
            token.Length > 0 && !token.Any(char.IsWhiteSpace));

    /// <summary>
    /// Returns true when the cwd is too shallow to support a folder-scoped
    /// approval grant. Mirrors the v1 minimum-depth check: a path with fewer
    /// than two non-empty segments under its root (e.g. <c>/</c>, <c>/etc/</c>,
    /// <c>C:\</c>) cannot be safely persisted as an ApprovalEntry directory.
    /// </summary>
    private static bool IsCwdTooShallow(string? cwd, ShellPathStyle pathStyle)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        return !ShellPathRules.TryGetRootRelativeDepth(cwd, pathStyle, out var depth)
               || depth < 2;
    }

    private static ToolApprovalMode GetMissingApprovalPolicyDefaultMode(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        TrustAudience audience,
        IToolApprovalMatcher matcher)
    {
        if (audience == TrustAudience.Personal && matcher.IsFailClosedOnPersonal(toolName, arguments))
            return ToolApprovalMode.Approval;

        return ToolApprovalMode.Auto;
    }

    private static ToolApprovalMode ResolveApprovalMode(
        ToolApprovalConfig? approvalPolicy,
        string approvalModeKey,
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        TrustAudience audience,
        IToolApprovalMatcher matcher)
    {
        if (approvalPolicy is null)
            return GetMissingApprovalPolicyDefaultMode(toolName, arguments, audience, matcher);

        // Matcher-derived argument-aware key (e.g. "file_write:control-plane").
        // This only fires when the matcher produced a distinct key; it is
        // unrelated to MCP tool names and therefore never consults
        // McpServerDefaults.
        if (!string.Equals(approvalModeKey, toolName.Value, StringComparison.Ordinal)
            && approvalPolicy.ToolOverrides.TryGetValue(approvalModeKey, out var matcherMode))
        {
            return matcherMode;
        }

        // No-matcher-key case shares the three-step precedence with
        // ToolApprovalConfig.GetEffectiveMode: exact ToolOverrides[toolName]
        // → McpServerDefaults[serverName] → fall-through. This keeps the
        // two callers consistent by construction.
        if (approvalPolicy.TryGetExplicitMode(toolName.Value, out var explicitMode))
            return explicitMode;

        if (audience == TrustAudience.Personal && matcher.IsFailClosedOnPersonal(toolName, arguments))
            return ToolApprovalMode.Approval;

        return approvalPolicy.DefaultMode;
    }

    private IToolApprovalMatcher SelectMatcherForTool(ToolName toolName)
    {
        if (string.Equals(toolName.Value, FileWriteTool.ToolName, StringComparison.Ordinal)
            || string.Equals(toolName.Value, FileEditTool.ToolName, StringComparison.Ordinal))
        {
            return _fileApprovalMatcher;
        }

        return DefaultApprovalMatcher.Instance;
    }

    private ShellExecutionMode ResolveShellMode()
        => _toolConfig.ShellMode ?? _defaults.ShellExecutionMode;

    private static TrustAudience ResolveAudience(EffectiveTrustContext? trustContext)
        => trustContext?.EffectiveAudience ?? TrustAudience.Public;

    private static TrustAudience ResolveAudience(ToolInvocationContext context)
        => context.Audience;

    private static bool IsShellTool(ToolRegistration registration)
        => registration.GrantCategory == "shell" || IsShellTool(registration.Tool);

    private static bool IsShellTool(INetclawTool tool)
        => string.Equals(tool.Name, ShellTool.ToolName, StringComparison.Ordinal);

    private static bool IsShellCoupledTool(INetclawTool tool)
        => IsShellTool(tool)
           || string.Equals(tool.Name, CheckBackgroundJobTool.ToolName, StringComparison.Ordinal);

    /// <summary>
    /// Returns true when the tool belongs to a subsystem whose feature flag is disabled.
    /// Disabled-subsystem tools are hidden for ALL audiences, not just Public.
    /// </summary>
    private bool IsFeatureDisabledTool(string toolName)
    {
        return toolName switch
        {
            "store_memory" or "find_memories" or "get_memories" or "update_memory"
                => !_featureGates.MemoryEnabled,
            "web_search" or "web_fetch"
                => !_featureGates.SearchEnabled,
            "skill_load" or "skill_read_resource"
                => !_featureGates.SkillSyncEnabled,
            "spawn_agent"
                => !_featureGates.SubAgentsEnabled,
            "set_reminder" or "cancel_reminder" or "list_reminders" or "get_reminder_history"
                => !_featureGates.SchedulingEnabled,
            _ => false
        };
    }

    private static string? GetToolName(AITool tool)
        => tool is AIFunction function ? function.Name : null;
}

/// <summary>
/// Subsystem feature flags consumed by <see cref="ToolAccessPolicy"/> to hide
/// tools belonging to disabled subsystems. All flags default to <c>true</c>.
/// </summary>
public sealed record FeatureGates(
    bool MemoryEnabled = true,
    bool SearchEnabled = true,
    bool SkillSyncEnabled = true,
    bool SubAgentsEnabled = true,
    bool SchedulingEnabled = true)
{
    /// <summary>All subsystems enabled — used as the default when no gates are supplied.</summary>
    public static readonly FeatureGates AllEnabled = new();
}

/// <summary>
/// Context for an approval-gated tool invocation. Contains the information
/// needed to present the approval prompt and cache the decision.
/// </summary>
public sealed record ToolApprovalContext(
    string ToolName,
    string DisplayText,
    IReadOnlyList<string> Patterns,
    // Verb-only projection of Candidates. Kept for renderers that bullet-
    // list verbs in the prompt body (Slack, Discord) without needing the
    // directory half.
    IReadOnlyList<string> CandidateVerbs,
    IReadOnlyList<ToolApprovalOption> Options,
    // Resolved cwd at the moment the gate decided approval was required.
    // Threaded through ToolInteractionRequest → PendingToolInteraction so
    // an "Always here" click persists with the actual directory rather
    // than a null sentinel (which would silently behave as "Always
    // anywhere").
    string? Cwd = null,
    // True when the invocation cannot be cleanly split into verb-chain
    // approval units (bash control-flow, unbalanced quotes/brackets).
    // Channel adapters use this to omit the persistent-grant buttons and
    // surface the "complex command" hint.
    bool IsMessy = false,
    // Per-clause (verb, directory) pairs for the persisted ApprovalEntry store.
    // The list includes path operands, redirect targets, and pipeline clauses.
    // A null directory uses Cwd. ApprovedAlways stores these effective scopes.
    IReadOnlyList<ApprovalCandidate>? Candidates = null)
{
    internal bool IsManagedTemporaryRetry { get; init; }

    internal string? ManagedTemporaryDirectory { get; init; }

    internal string? PlatformTemporaryRoot { get; init; }
}

public sealed record ToolApprovalOption(ApprovalOptionKey Key, string Label);

public sealed class ToolAccessDeniedException : InvalidOperationException
{
    public ToolAccessDeniedException(string denyReason)
        : this(denyReason, null)
    {
    }

    internal ToolAccessDeniedException(string denyReason, string? denyMessage)
        : base(denyMessage ?? denyReason)
    {
        DenyReason = denyReason;
        DenyMessage = denyMessage;
    }

    public string DenyReason { get; }

    internal string? DenyMessage { get; }

    internal string ToAgentResult() => DenyMessage ?? $"Tool access denied: {DenyReason}";
}

/// <summary>
/// Thrown by the executor when a tool invocation requires interactive user
/// approval before execution. Caught by the pipeline to initiate the
/// approval flow.
/// </summary>
public sealed class ToolApprovalRequiredException : InvalidOperationException
{
    public ToolApprovalRequiredException(ToolApprovalContext context)
        : this(context, correction: null)
    {
    }

    internal ToolApprovalRequiredException(
        ToolApprovalContext context,
        ToolCorrection? correction)
        : base($"Tool '{context.ToolName}' requires approval")
    {
        ApprovalContext = context;
        Correction = correction;
    }

    public ToolApprovalContext ApprovalContext { get; }

    internal ToolCorrection? Correction { get; }
}
