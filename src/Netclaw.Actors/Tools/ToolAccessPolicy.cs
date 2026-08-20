// -----------------------------------------------------------------------
// <copyright file="ToolAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

public sealed class ToolAccessPolicy
{
    private static readonly IReadOnlyList<ToolApprovalOption> SessionScratchRetryOptions =
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
    private readonly IShellTrustZonePolicy? _shellTrustZonePolicy;
    private readonly IToolApprovalMatcher _fileApprovalMatcher;
    private readonly FeatureGates _featureGates;
    private readonly ScopedShellSafeVerbPolicy? _safeVerbPolicy;
    private readonly PlatformTemporaryScopePolicy _platformTemporaryScopePolicy;
    private readonly ConditionalWeakTable<ToolExecutionContext, SessionScratchRetryMarker>
        _sessionScratchRetries = new();

    internal ApprovalShell Shell => _shellCommandPolicy.Environment.Grammar == ShellGrammar.Bash
        ? ApprovalShell.Bash
        : ApprovalShell.PowerShell;

    internal ShellExecutionEnvironment ShellEnvironment => _shellCommandPolicy.Environment;

    internal bool IsSafePlatformTemporaryPath(string path)
        => _platformTemporaryScopePolicy.IsSafePlatformTemporaryPath(path);

    public ToolAccessPolicy(
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy shellCommandPolicy,
        ToolPathPolicy toolPathPolicy,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        FeatureGates? featureGates = null,
        IShellTrustZonePolicy? shellTrustZonePolicy = null,
        SafeVerbList? safeVerbs = null)
        : this(
            toolConfig,
            defaults,
            shellCommandPolicy,
            toolPathPolicy,
            PlatformTemporaryScopePolicy.Create(shellCommandPolicy.Environment),
            fileApprovalMatcher,
            featureGates,
            shellTrustZonePolicy,
            safeVerbs)
    {
    }

    internal ToolAccessPolicy(
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy shellCommandPolicy,
        ToolPathPolicy toolPathPolicy,
        PlatformTemporaryScopePolicy platformTemporaryScopePolicy,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        FeatureGates? featureGates = null,
        IShellTrustZonePolicy? shellTrustZonePolicy = null,
        SafeVerbList? safeVerbs = null)
    {
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
        _shellTrustZonePolicy = shellTrustZonePolicy;
        _fileApprovalMatcher = fileApprovalMatcher ?? DefaultApprovalMatcher.Instance;
        _featureGates = featureGates ?? FeatureGates.AllEnabled;
        _safeVerbPolicy = safeVerbs is not null ? new ScopedShellSafeVerbPolicy(safeVerbs) : null;
        _platformTemporaryScopePolicy = platformTemporaryScopePolicy;
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

    public ToolAccessDecision AuthorizeInvocation(INetclawTool tool, ToolExecutionContext context)
        => AuthorizeInvocation(tool, context, arguments: null);

    public ToolAccessDecision AuthorizeInvocation(
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
                ShellEnvironment)
            : new ShellPolicyPreflightResult.Complete(
                ToolAccessDecision.Deny("internal_policy_failure"),
                authorizedAnalysis: null);
    }

    private ToolAccessDecision AuthorizeInvocationCore(
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments,
        bool deferReviewedSafeCoverage,
        out ShellCommandAnalysis? authorizedAnalysis)
    {
        authorizedAnalysis = null;

        if (tool is McpToolAdapter mcp)
        {
            if (!_profileResolver.IsMcpServerAllowed(new McpServerName(mcp.ServerName), context.Invocation))
                return ToolAccessDecision.Deny("mcp_server_not_allowed_for_audience_profile");

            if (!_profileResolver.IsMcpToolAllowed(new McpServerName(mcp.ServerName), new ToolName(mcp.BareToolName), context.Invocation))
                return ToolAccessDecision.Deny("mcp_tool_not_allowed_for_audience_profile");

            var mcpToolName = new ToolName(tool.Name);
            var (_, approvalArguments) = ToolCallMeta.ExtractFrom(
                arguments,
                key => ToolArgumentValidator.ResolveMetaField(mcp, key));
            var approvalMode = GetApprovalMode(
                mcpToolName,
                context,
                approvalArguments,
                McpApprovalMatcher.Instance);
            return CheckApprovalGate(
                mcpToolName,
                context,
                approvalArguments,
                McpApprovalMatcher.Instance,
                approvalMode);
        }

        var toolName = new ToolName(tool.Name);

        if (!_profileResolver.IsToolAllowed(toolName, context.Invocation))
            return ToolAccessDecision.Deny("tool_not_allowed_for_audience_profile");

        if (!IsShellCoupledTool(tool))
        {
            var matcher = SelectMatcherForTool(toolName);
            var approvalMode = GetApprovalMode(toolName, context, arguments, matcher);
            return CheckApprovalGate(toolName, context, arguments, matcher, approvalMode);
        }

        var shellMode = ResolveShellMode();
        if (shellMode == ShellExecutionMode.Off)
            return ToolAccessDecision.Deny("shell_disabled");

        if (shellMode == ShellExecutionMode.SandboxOnly)
            return ToolAccessDecision.Deny("shell_requires_sandbox_backend");

        var shellAudience = ResolveAudience(context.Invocation);
        if (shellAudience != TrustAudience.Personal)
            return ToolAccessDecision.Deny("shell_requires_personal_context");

        // shell_execute authorizes the process before the job starts. This tool
        // can only control a job with the same session, audience, and boundary.
        // It does not create a new shell invocation or require another approval.
        if (string.Equals(tool.Name, CheckBackgroundJobTool.ToolName, StringComparison.Ordinal))
            return ToolAccessDecision.Allow(ToolAllowReason.BackgroundJobLifecycle);

        var shellCommand = ExtractShellCommand(arguments);
        var workingDirectory = context.ResolveShellCwd(ExtractWorkingDirectory(arguments));
        ShellCommandAnalysis? shellAnalysis = null;
        if (shellCommand is not null)
        {
            shellAnalysis = _shellCommandPolicy.Analyze(shellCommand, workingDirectory);
            var hardDenyDecision = _shellCommandPolicy.Evaluate(shellAnalysis);
            if (!hardDenyDecision.Allowed)
                return ToolAccessDecision.Deny(
                    $"hard_deny_{hardDenyDecision.DenyCategory?.ToWireName() ?? "unknown"}");

            if (_toolPathPolicy.CommandReferencesDeniedPath(shellAnalysis))
                return ToolAccessDecision.Deny("shell_references_protected_path");
        }

        var mode = GetApprovalMode(toolName, context, arguments, _shellApprovalMatcher);
        var approvalModeDecision = GetApprovalModeDecision(mode);
        if (approvalModeDecision is { Allowed: false })
            return approvalModeDecision;

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

        // Non-interactive channels: sandbox shell commands to trust zone paths.
        // Even if the verb-chain is pre-approved, path arguments must fall within
        // the channel's allowed filesystem roots. Fail-closed: if no trust zone
        // policy is configured, deny any shell command with path arguments.
        if (context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Unavailable && shellCommand is not null)
        {
            if (_shellTrustZonePolicy is null)
            {
                if (ShellCommandHasTrustZoneSensitiveInputs(shellApproval, workingDirectory))
                    return ToolAccessDecision.Deny("shell_trust_zone_policy_not_configured");
            }
            else
            {
                var trustZoneDeny = EnforceShellTrustZones(
                    shellApproval!,
                    workingDirectory,
                    context);
                if (trustZoneDeny is not null)
                    return trustZoneDeny;
            }
        }

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

    internal void MarkSessionScratchRetry(
        ToolExecutionContext context,
        ToolAgentCorrection.SessionScratchSuggested correction)
    {
        _sessionScratchRetries.Remove(context);
        _sessionScratchRetries.Add(context, new SessionScratchRetryMarker(correction));
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

        if (_platformTemporaryScopePolicy.IsSafePlatformTemporaryPath(normalized))
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
    /// For non-interactive channels, validates that the working directory and all
    /// path-like arguments in a shell command are write-authorized for the channel's
    /// audience, using the same audience-scoped resolution as <c>file_write</c>
    /// (<c>Mode.All</c> ⇒ unrestricted, <c>Mode.Roots</c> ⇒ confined to roots,
    /// <c>Mode.None</c> ⇒ denied). Returns a deny decision if any path escapes, or
    /// null if all paths are within bounds.
    /// </summary>
    private ToolAccessDecision? EnforceShellTrustZones(
        ShellApprovalAnalysis approval,
        string? workingDirectory,
        ToolExecutionContext context)
    {
        if (approval.IsMessy)
            return ToolAccessDecision.Deny("shell_unresolved_trust_zone_input");

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var expandedWorkingDirectory = PathUtility.ExpandAndNormalize(workingDirectory, workingDirectory: null);
            if (expandedWorkingDirectory is null)
                return ToolAccessDecision.Deny("shell_invalid_working_directory");

            if (!_shellTrustZonePolicy!.IsShellWritePathAuthorized(expandedWorkingDirectory, context.Invocation))
                return ToolAccessDecision.Deny("shell_working_directory_outside_trust_zone");
        }

        foreach (var directory in approval.Candidates
                     .Select(static candidate => candidate.Directory)
                     .Where(static directory => !string.IsNullOrWhiteSpace(directory))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!_shellTrustZonePolicy!.IsShellWritePathAuthorized(directory!, context.Invocation))
                return ToolAccessDecision.Deny("shell_path_outside_trust_zone");
        }

        return null;
    }

    private static bool ShellCommandHasTrustZoneSensitiveInputs(
        ShellApprovalAnalysis? approval,
        string? workingDirectory)
        => !string.IsNullOrWhiteSpace(workingDirectory)
           || approval is null
           || approval.IsMessy
           || approval.Candidates.Any(static candidate =>
               !string.IsNullOrWhiteSpace(candidate.Directory));

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

    private ToolAccessDecision CheckApprovalGate(
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
        string? suggestedProjectDirectory = null;
        ToolAgentCorrection? agentCorrection = null;

        if (isShell && shellAnalysis is not null)
        {
            agentCorrection = _platformTemporaryScopePolicy.Evaluate(
                shellAnalysis,
                approvalCandidates,
                arguments,
                context.Invocation);
        }

        // A clean shell command can combine safe candidates with candidates
        // that need a stored grant. Remove only candidates that independently
        // satisfy both the safe-verb and safe-space rules. The approval store
        // must still cover every remaining candidate.
        if (_safeVerbPolicy is not null
            && isShell
            && !isMessy
            && approvalCandidates.Count > 0)
        {
            if (agentCorrection is null
                && !_platformTemporaryScopePolicy.IsPlatformTemporaryRoot(context.Approval.Cwd)
                && _safeVerbPolicy.CanShortCircuitAfterProjectDeclaration(
                    approvalCandidates,
                    context.Approval.Cwd,
                    context.Invocation))
            {
                suggestedProjectDirectory = context.Approval.Cwd;
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
                    return ToolAccessDecision.Allow(ToolAllowReason.SafeVerbInTrustedScope);
            }
        }

        var candidateVerbs = approvalCandidates
            .Select(static candidate => candidate.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var isSessionScratchRetry = _sessionScratchRetries.TryGetValue(context, out var retryMarker);
        var options = isSessionScratchRetry
            ? SessionScratchRetryOptions
            : BuildApprovalOptions(
                isMessy,
                hasReusablePhraseForEveryCandidate: !isShell ||
                    approvalCandidates.All(HasReusableShellPhrase),
                isCwdShallow: IsCwdTooShallow(context.Approval.Cwd, ShellEnvironment.PathStyle),
                allEffectiveDirsAreSessionScratch: AllCandidatesResolveToSessionScratch(
                    approvalCandidates, context.Approval.Cwd, context.SessionDirectory),
                supportsDirectoryScope: matcher is ShellApprovalMatcher,
                isMcpTool: toolName.IsMcp);

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
            SuggestedProjectDirectory = suggestedProjectDirectory,
            AgentCorrection = isSessionScratchRetry ? null : agentCorrection,
            IsSessionScratchRetry = isSessionScratchRetry,
            SessionScratchDirectory = retryMarker?.Correction.SessionDirectory,
            PlatformTemporaryRoot = retryMarker?.Correction.TemporaryRoot
        };

        return ToolAccessDecision.RequiresApproval(approvalContext);
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

    private static ToolAccessDecision? GetApprovalModeDecision(ToolApprovalMode mode)
        => mode switch
        {
            ToolApprovalMode.Approval => null,
            ToolApprovalMode.Auto => ToolAccessDecision.Allow(ToolAllowReason.PolicyAuto),
            ToolApprovalMode.Deny => ToolAccessDecision.Deny("tool_denied_by_approval_policy"),
            _ => ToolAccessDecision.Deny("internal_policy_failure")
        };

    internal static ToolApprovalContext NarrowShellApprovalContext(
        ToolApprovalContext context,
        IReadOnlyList<ApprovalCandidate> unapprovedCandidates,
        string? sessionDirectory,
        ShellPathStyle pathStyle)
    {
        var candidateVerbs = unapprovedCandidates
            .Select(static candidate => candidate.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = context.IsSessionScratchRetry
            ? SessionScratchRetryOptions
            : BuildApprovalOptions(
                isMessy: false,
                hasReusablePhraseForEveryCandidate:
                    unapprovedCandidates.All(HasReusableShellPhrase),
                isCwdShallow: IsCwdTooShallow(context.Cwd, pathStyle),
                allEffectiveDirsAreSessionScratch: AllCandidatesResolveToSessionScratch(
                    unapprovedCandidates, context.Cwd, sessionDirectory),
                supportsDirectoryScope: true,
                isMcpTool: false);

        return context with
        {
            Patterns = candidateVerbs,
            CandidateVerbs = candidateVerbs,
            Candidates = unapprovedCandidates,
            Options = options
        };
    }

    /// <summary>
    /// Returns true when every candidate's effective directory resolves to
    /// the session's ephemeral <c>session_dir</c>. Persisting an "Always
    /// here" grant scoped to that directory is dead-on-arrival because the
    /// next session has a fresh session_dir; matching against the saved
    /// entry would never succeed. The button is hidden in that case so
    /// operators can pick "This chat" (the equivalent in-session
    /// semantics) or "Always anywhere" (folder-agnostic) instead.
    /// </summary>
    private static bool AllCandidatesResolveToSessionScratch(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        string? sessionDirectory)
    {
        if (string.IsNullOrEmpty(sessionDirectory) || candidates.Count == 0)
            return false;

        foreach (var candidate in candidates)
        {
            var effective = candidate.Directory ?? cwd;
            if (string.IsNullOrEmpty(effective))
                return false;

            if (!PathUtility.AreEquivalentPaths(effective, sessionDirectory))
                return false;
        }

        return true;
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
    /// <item><b>Session-scratch effective directory</b> (every candidate's
    /// effective directory is the session's ephemeral <c>session_dir</c>) —
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
    private static IReadOnlyList<ToolApprovalOption> BuildApprovalOptions(
        bool isMessy,
        bool hasReusablePhraseForEveryCandidate,
        bool isCwdShallow,
        bool allEffectiveDirsAreSessionScratch,
        bool supportsDirectoryScope,
        bool isMcpTool)
    {
        if (isMessy || !hasReusablePhraseForEveryCandidate)
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

        if (supportsDirectoryScope && !isCwdShallow && !allEffectiveDirsAreSessionScratch)
        {
            options.Add(new ToolApprovalOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel));
        }

        options.Add(new ToolApprovalOption(
            ApprovalOptionKeys.ApproveEverywhereKey,
            ApprovalOptionKeys.LabelFor(ApprovalOptionKeys.ApproveEverywhere, isMcpTool)));
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

public sealed record ToolAccessDecision(bool Allowed, string? DenyReason = null, ToolApprovalContext? ApprovalContext = null)
{
    /// <summary>
    /// Gets the reason for an allowed access decision.
    /// </summary>
    internal ToolAllowReason? AllowReason { get; private init; }

    /// <summary>True when the decision is <see cref="RequiresApproval"/>.</summary>
    public bool NeedsApproval => ApprovalContext is not null && Allowed;

    public static ToolAccessDecision Allow() => new(true);

    internal static ToolAccessDecision Allow(ToolAllowReason reason) => new(true) { AllowReason = reason };

    public static ToolAccessDecision Deny(string reason) => new(false, reason);

    public static ToolAccessDecision RequiresApproval(ToolApprovalContext context) => new(true, null, context);
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
    /// <summary>
    /// Gets the exact shell cwd that the agent can declare through
    /// <c>set_working_directory</c> when undeclared scope is the only obstacle
    /// to the reviewed-safe policy.
    /// </summary>
    internal string? SuggestedProjectDirectory { get; init; }

    internal ToolAgentCorrection? AgentCorrection { get; init; }

    internal bool IsSessionScratchRetry { get; init; }

    internal string? SessionScratchDirectory { get; init; }

    internal string? PlatformTemporaryRoot { get; init; }
}

internal sealed class SessionScratchRetryMarker(
    ToolAgentCorrection.SessionScratchSuggested correction)
{
    internal ToolAgentCorrection.SessionScratchSuggested Correction { get; } = correction;
}

public sealed record ToolApprovalOption(ApprovalOptionKey Key, string Label);

public sealed class ToolAccessDeniedException : InvalidOperationException
{
    public ToolAccessDeniedException(string denyReason)
        : base(denyReason)
    {
        DenyReason = denyReason;
    }

    public string DenyReason { get; }
}

/// <summary>
/// Thrown by the executor when a tool invocation requires interactive user
/// approval before execution. Caught by the pipeline to initiate the
/// approval flow.
/// </summary>
public sealed class ToolApprovalRequiredException : InvalidOperationException
{
    public ToolApprovalRequiredException(ToolApprovalContext context)
        : base($"Tool '{context.ToolName}' requires approval")
    {
        ApprovalContext = context;
    }

    public ToolApprovalContext ApprovalContext { get; }
}
