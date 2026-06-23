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

namespace Netclaw.Actors.Tools;

public sealed class ToolAccessPolicy
{
    private readonly ToolConfig _toolConfig;
    private readonly EffectivePolicyDefaults _defaults;
    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly ShellCommandPolicy? _shellCommandPolicy;
    private readonly ToolPathPolicy? _toolPathPolicy;
    private readonly IShellTrustZonePolicy? _shellTrustZonePolicy;
    private readonly IToolApprovalMatcher _fileApprovalMatcher;
    private readonly FeatureGates _featureGates;
    private readonly ScopedShellSafeVerbPolicy? _safeVerbPolicy;

    public ToolAccessPolicy(
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy? shellCommandPolicy = null,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        ToolPathPolicy? toolPathPolicy = null,
        FeatureGates? featureGates = null,
        IShellTrustZonePolicy? shellTrustZonePolicy = null,
        SafeVerbList? safeVerbs = null)
    {
        _toolConfig = toolConfig;
        _defaults = defaults;
        _profileResolver = new ToolAudienceProfileResolver(toolConfig);
        _shellCommandPolicy = shellCommandPolicy;
        _toolPathPolicy = toolPathPolicy;
        _shellTrustZonePolicy = shellTrustZonePolicy;
        _fileApprovalMatcher = fileApprovalMatcher ?? DefaultApprovalMatcher.Instance;
        _featureGates = featureGates ?? FeatureGates.AllEnabled;
        _safeVerbPolicy = safeVerbs is not null ? new ScopedShellSafeVerbPolicy(safeVerbs) : null;
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
        ToolExecutionContext? context)
        => tools.Where(tool => IsToolExposed(tool, context)).ToList();

    public bool IsToolExposed(ToolRegistration registration, EffectiveTrustContext? trustContext)
        => IsToolExposed(registration.Tool, ResolveAudience(trustContext));

    public bool IsToolExposed(INetclawTool tool, ToolExecutionContext? context)
        => IsToolExposed(tool, ResolveAudience(context));

    private bool IsToolExposed(INetclawTool tool, TrustAudience audience)
    {
        // Feature-disabled tools are hidden for ALL audiences
        if (IsFeatureDisabledTool(tool.Name))
            return false;

        if (tool is McpToolAdapter mcp)
            return _profileResolver.IsMcpServerAllowed(new McpServerName(mcp.ServerName), audience)
                && _profileResolver.IsMcpToolAllowed(new McpServerName(mcp.ServerName), new ToolName(mcp.BareToolName), audience);

        if (!_profileResolver.IsToolAllowed(new ToolName(tool.Name), CreateContext(audience)))
            return false;

        if (IsShellCoupledTool(tool))
            return ResolveShellMode() == ShellExecutionMode.HostAllowed && audience == TrustAudience.Personal;

        return true;
    }

    public ToolAccessDecision AuthorizeInvocation(INetclawTool tool, ToolExecutionContext? context)
        => AuthorizeInvocation(tool, context, arguments: null);

    public ToolAccessDecision AuthorizeInvocation(
        INetclawTool tool,
        ToolExecutionContext? context,
        IDictionary<string, object?>? arguments)
    {
        if (tool is McpToolAdapter mcp)
        {
            if (!_profileResolver.IsMcpServerAllowed(new McpServerName(mcp.ServerName), context))
                return ToolAccessDecision.Deny("mcp_server_not_allowed_for_audience_profile");

            if (!_profileResolver.IsMcpToolAllowed(new McpServerName(mcp.ServerName), new ToolName(mcp.BareToolName), context))
                return ToolAccessDecision.Deny("mcp_tool_not_allowed_for_audience_profile");

            var mcpToolName = new ToolName(tool.Name);
            return CheckApprovalGate(mcpToolName, context, arguments, DefaultApprovalMatcher.Instance);
        }

        var toolName = new ToolName(tool.Name);

        if (!_profileResolver.IsToolAllowed(toolName, context))
            return ToolAccessDecision.Deny("tool_not_allowed_for_audience_profile");

        if (!IsShellCoupledTool(tool))
            return CheckApprovalGate(toolName, context, arguments, SelectMatcherForTool(toolName));

        var shellMode = ResolveShellMode();
        if (shellMode == ShellExecutionMode.Off)
            return ToolAccessDecision.Deny("shell_disabled");

        if (shellMode == ShellExecutionMode.SandboxOnly)
            return ToolAccessDecision.Deny("shell_requires_sandbox_backend");

        var shellAudience = ResolveAudience(context);
        if (shellAudience != TrustAudience.Personal)
            return ToolAccessDecision.Deny("shell_requires_personal_context");

        var shellCommand = ExtractShellCommand(arguments);
        if (_shellCommandPolicy is not null && shellCommand is not null)
        {
            var hardDenyDecision = _shellCommandPolicy.Evaluate(shellCommand);
            if (!hardDenyDecision.Allowed)
                return ToolAccessDecision.Deny(
                    $"hard_deny_{hardDenyDecision.DenyCategory?.ToWireName() ?? "unknown"}");
        }

        var workingDirectory = ExtractWorkingDirectory(arguments);
        if (shellCommand is not null
            && _toolPathPolicy?.CommandReferencesDeniedPath(shellCommand, workingDirectory) == true)
            return ToolAccessDecision.Deny("shell_references_protected_path");

        // Non-interactive channels: sandbox shell commands to trust zone paths.
        // Even if the verb-chain is pre-approved, path arguments must fall within
        // the channel's allowed filesystem roots. Fail-closed: if no trust zone
        // policy is configured, deny any shell command with path arguments.
        if (context?.SupportsInteractiveApproval == false && shellCommand is not null)
        {
            if (_shellTrustZonePolicy is null)
            {
                if (ShellCommandHasTrustZoneSensitiveInputs(shellCommand, workingDirectory))
                    return ToolAccessDecision.Deny("shell_trust_zone_policy_not_configured");
            }
            else
            {
                var trustZoneDeny = EnforceShellTrustZones(shellCommand, workingDirectory, context);
                if (trustZoneDeny is not null)
                    return trustZoneDeny;
            }
        }

        return CheckApprovalGate(toolName, context, arguments, ShellApprovalMatcher.Instance);
    }

    /// <summary>
    /// For non-interactive channels, validates that the working directory and all
    /// path-like arguments in a shell command are write-authorized for the channel's
    /// audience, using the same audience-scoped resolution as <c>file_write</c>
    /// (<c>Mode.All</c> ⇒ unrestricted, <c>Mode.Roots</c> ⇒ confined to roots,
    /// <c>Mode.None</c> ⇒ denied). Returns a deny decision if any path escapes, or
    /// null if all paths are within bounds.
    /// </summary>
    private ToolAccessDecision? EnforceShellTrustZones(
        string shellCommand,
        string? workingDirectory,
        ToolExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var expandedWorkingDirectory = PathUtility.ExpandAndNormalize(workingDirectory, workingDirectory: null);
            if (expandedWorkingDirectory is null)
                return ToolAccessDecision.Deny("shell_invalid_working_directory");

            if (!_shellTrustZonePolicy!.IsShellWritePathAuthorized(expandedWorkingDirectory, context))
                return ToolAccessDecision.Deny("shell_working_directory_outside_trust_zone");
        }

        var pathTokens = ExtractShellPathTokens(shellCommand);
        if (pathTokens.Count == 0)
            return null;

        foreach (var pathToken in pathTokens)
        {
            var expanded = ShellTokenizer.NormalizePathToken(pathToken, workingDirectory);
            if (expanded is null)
                continue;

            if (!_shellTrustZonePolicy!.IsShellWritePathAuthorized(expanded, context))
                return ToolAccessDecision.Deny("shell_path_outside_trust_zone");
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractShellPathTokens(string shellCommand)
    {
        var pathTokens = new List<string>();
        foreach (var segment in ShellTokenizer.GetAllCommandSegments(shellCommand))
        {
            foreach (var token in ShellTokenizer.Tokenize(segment))
            {
                var trimmed = TrimShellTokenPunctuation(token);
                if (ShellTokenizer.LooksLikePath(trimmed))
                    pathTokens.Add(trimmed);
            }
        }

        return pathTokens;
    }

    private static bool ShellCommandHasTrustZoneSensitiveInputs(string shellCommand, string? workingDirectory)
        => !string.IsNullOrWhiteSpace(workingDirectory) || ShellCommandHasPathArguments(shellCommand);

    private static string TrimShellTokenPunctuation(string token)
        => token.Trim().TrimStart(';', '|', '&').TrimEnd(';', '|', '&');

    private static bool ShellCommandHasPathArguments(string shellCommand)
    {
        foreach (var token in ExtractShellPathTokens(shellCommand))
        {
            if (!string.IsNullOrWhiteSpace(token))
                return true;
        }

        return false;
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

    private ToolAccessDecision CheckApprovalGate(
        ToolName toolName,
        ToolExecutionContext? context,
        IDictionary<string, object?>? arguments,
        IToolApprovalMatcher matcher)
    {
        var audience = ResolveAudience(context);
        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(_toolConfig.AudienceProfiles, audience);
        var approvalPolicy = profile.ApprovalPolicy;
        var approvalModeKey = matcher.GetApprovalModeKey(toolName, arguments);
        var mode = ResolveApprovalMode(
            approvalPolicy,
            approvalModeKey,
            toolName,
            arguments,
            audience,
            matcher);

        if (mode == ToolApprovalMode.Deny)
            return ToolAccessDecision.Deny("tool_denied_by_approval_policy");

        if (mode == ToolApprovalMode.Auto)
            return ToolAccessDecision.Allow();

        // The approval policy is authoritative for every channel — there is no
        // safe-list auto-grant for non-interactive callers. A non-interactive
        // caller (reminder, webhook, sub-agent without an approval bridge) that
        // hits an approval-gated tool fails closed unless the patterns are
        // already in the persistent approval store.

        // Approval prompts carry three views of the invocation:
        // - `patterns`: the exact blocked units shown to the user and reused by
        //   approve-once retries.
        // - `candidates`: the (verb, directory) pairs evaluated against
        //   persisted ApprovalEntry records by the gate. The directory half is
        //   the path argument extracted from each clause when present, falling
        //   back to ToolExecutionContext.Cwd at evaluation time.
        // - `candidateVerbs`: the verb-only projection of `candidates`, kept
        //   for renderers (Slack/Discord builders) that bullet-list verbs in
        //   the prompt body. Button labels stay fixed; runtime values like
        //   paths never enter button text because Slack caps button text at
        //   76 chars and Discord at 80.
        var patterns = matcher.ExtractPatterns(toolName, arguments);
        var candidates = matcher.ExtractCandidates(toolName, arguments);
        var candidateVerbs = candidates
            .Select(static c => c.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayText = matcher.FormatForDisplay(toolName, arguments);
        var isMessy = matcher.IsMessy(toolName, arguments);

        // Resolve cwd up-front for shell so it's available to the safe-verb
        // short-circuit, the shallow-cwd guard, AND the approval context that
        // gets persisted on "Always here". Doing this only inside the
        // short-circuit branch (as the original v2 layout did) drops cwd from
        // ToolApprovalContext when conditions don't match — silently turning
        // every "Always here" click into "Always anywhere" because the
        // persistence path reads PendingToolInteraction.Cwd.
        var isShell = string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal);
        if (isShell && context is not null)
            context.Cwd = context.ResolveShellCwd(ExtractWorkingDirectory(arguments));

        // Safe-verb ∩ safe-space short-circuit. Runs only for shell and only
        // when the matcher could extract candidate verbs cleanly — messy
        // commands always prompt regardless of verb membership. Auto-allows
        // demonstrably read-only verbs (cat/ls/grep/find/git status/...)
        // when the cwd is inside session_dir or project_dir.
        if (_safeVerbPolicy is not null
            && context is not null
            && isShell
            && !isMessy
            && candidateVerbs.Count > 0
            && _safeVerbPolicy.AllShortCircuit(candidateVerbs, context.Cwd, context))
        {
            return ToolAccessDecision.Allow();
        }

        var options = BuildApprovalOptions(
            isMessy,
            isCwdShallow: IsCwdTooShallow(context?.Cwd),
            allEffectiveDirsAreSessionScratch: AllCandidatesResolveToSessionScratch(
                candidates, context?.Cwd, context?.SessionDirectory));

        var approvalContext = new ToolApprovalContext(
            toolName.Value,
            displayText,
            patterns,
            candidateVerbs,
            options,
            Cwd: context?.Cwd,
            IsMessy: isMessy,
            Candidates: candidates);

        return ToolAccessDecision.RequiresApproval(approvalContext);
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
    /// </list>
    /// </summary>
    private static IReadOnlyList<ToolApprovalOption> BuildApprovalOptions(
        bool isMessy,
        bool isCwdShallow,
        bool allEffectiveDirsAreSessionScratch)
    {
        if (isMessy)
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

        if (!isCwdShallow && !allEffectiveDirsAreSessionScratch)
        {
            options.Add(new ToolApprovalOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel));
        }

        options.Add(new ToolApprovalOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel));
        options.Add(new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel));

        return options;
    }

    /// <summary>
    /// Returns true when the cwd is too shallow to support a folder-scoped
    /// approval grant. Mirrors the v1 minimum-depth check: a path with fewer
    /// than two non-empty segments under its root (e.g. <c>/</c>, <c>/etc/</c>,
    /// <c>C:\</c>) cannot be safely persisted as an ApprovalEntry directory.
    /// </summary>
    private static bool IsCwdTooShallow(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        try
        {
            var segments = PathUtility.Normalize(cwd).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            // POSIX root → 0 segments after trim. /etc → 1 segment. /home/user → 2 segments.
            // Windows: C:\ → ["C:"] → 1 segment but conventionally a root, so still shallow.
            //          C:\Users\foo → 3 segments.
            // Require at least 2 distinct path segments for folder-scoped persistence.
            return segments.Length < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Treat unparseable cwds as shallow — fail closed on the
            // persistent button rather than offering a grant whose target we
            // could not normalize.
            return true;
        }
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

    private static TrustAudience ResolveAudience(ToolExecutionContext? context)
        => SecurityPolicyDefaults.ResolveAudienceWithFallback(context?.Audience, context?.SessionId);

    private static ToolExecutionContext CreateContext(TrustAudience audience)
        => new(null, null) { Audience = audience };

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
    /// <summary>True when the decision is <see cref="RequiresApproval"/>.</summary>
    public bool NeedsApproval => ApprovalContext is not null && Allowed;

    public static ToolAccessDecision Allow() => new(true);

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
    // Per-clause (verb, directory) pairs evaluated against the persisted
    // ApprovalEntry store. The directory half is the path argument
    // extracted from the clause when present, falling back to Cwd at
    // match time. The persistence path reads this on ApprovedAlways so
    // "Always here" stores per-clause folder-scoped grants from the
    // actual paths the agent touched.
    IReadOnlyList<ApprovalCandidate>? Candidates = null);

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
