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

    public ToolAccessPolicy(
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy? shellCommandPolicy = null,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        ToolPathPolicy? toolPathPolicy = null,
        FeatureGates? featureGates = null,
        IShellTrustZonePolicy? shellTrustZonePolicy = null)
    {
        _toolConfig = toolConfig;
        _defaults = defaults;
        _profileResolver = new ToolAudienceProfileResolver(toolConfig);
        _shellCommandPolicy = shellCommandPolicy;
        _toolPathPolicy = toolPathPolicy;
        _shellTrustZonePolicy = shellTrustZonePolicy;
        _fileApprovalMatcher = fileApprovalMatcher ?? DefaultApprovalMatcher.Instance;
        _featureGates = featureGates ?? FeatureGates.AllEnabled;
    }

    public int MaxToolTimeoutSeconds => _toolConfig.MaxToolTimeoutSeconds;

    public int ShellTimeoutSeconds => _toolConfig.ShellTimeoutSeconds;

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
                return ToolAccessDecision.Deny($"hard_deny_{hardDenyDecision.DenyCategory ?? "unknown"}");
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
    /// For non-interactive channels, validates that all path-like arguments in a shell
    /// command fall within the trust zone roots for the channel's audience. Returns a
    /// deny decision if any path escapes, or null if all paths are within bounds.
    /// </summary>
    private ToolAccessDecision? EnforceShellTrustZones(
        string shellCommand,
        string? workingDirectory,
        ToolExecutionContext context)
    {
        var roots = _shellTrustZonePolicy!.GetTrustZoneRoots(context);
        if (roots.Count == 0)
            return ToolAccessDecision.Deny("shell_no_trust_zone_roots");

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var expandedWorkingDirectory = ExpandShellPath(workingDirectory, workingDirectory: null);
            if (expandedWorkingDirectory is null)
                return ToolAccessDecision.Deny("shell_invalid_working_directory");

            if (!IsPathWithinAnyRoot(expandedWorkingDirectory, roots))
                return ToolAccessDecision.Deny("shell_working_directory_outside_trust_zone");
        }

        var pathTokens = ExtractShellPathTokens(shellCommand);
        if (pathTokens.Count == 0)
            return null;

        foreach (var pathToken in pathTokens)
        {
            var expanded = ExpandShellPath(pathToken, workingDirectory);
            if (expanded is null)
                continue;

            if (!IsPathWithinAnyRoot(expanded, roots))
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

    private static string? ExpandShellPath(string token, string? workingDirectory)
    {
        var expanded = token;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (expanded.StartsWith('~'))
        {
            if (string.IsNullOrWhiteSpace(home))
                return null;
            expanded = expanded.Length == 1
                ? home
                : Path.Combine(home, expanded[1..].TrimStart('/', '\\'));
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            expanded = expanded.Replace("$HOME", home, StringComparison.OrdinalIgnoreCase);
            expanded = expanded.Replace("${HOME}", home, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var baseDir = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : Environment.CurrentDirectory;

            return Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(baseDir, expanded));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathWithinAnyRoot(string fullPath, IReadOnlyList<string> roots)
    {
        var normalized = NormalizeDirectoryComparisonPath(fullPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var root in roots)
        {
            var normalizedRoot = NormalizeDirectoryComparisonPath(root);
            if (!normalized.StartsWith(normalizedRoot, comparison))
                continue;
            if (normalized.Length == normalizedRoot.Length)
                return true;
            var boundary = normalized[normalizedRoot.Length];
            if (boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar)
                return true;
        }

        return false;
    }

    private static string NormalizeDirectoryComparisonPath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
        if (arguments is null)
            return null;

        if (arguments.TryGetValue("Command", out var command) && command is string text)
            return text;

        if (arguments.TryGetValue("command", out command) && command is string lowerText)
            return lowerText;

        return null;
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

        // Non-interactive channels (reminders, webhooks, sub-agents without parent
        // approval channel): tools on the safe list are auto-granted. Everything else
        // falls through to the normal approval extraction path — the executor will
        // check the persistent approval store and allow if all patterns are pre-approved.
        if (context?.SupportsInteractiveApproval == false
            && SubAgentToolPolicy.IsAllowedForUserFacing(toolName.Value))
        {
            return ToolAccessDecision.Allow();
        }

        var allPatterns = matcher.ExtractPatterns(toolName, arguments);
        var displayText = matcher.FormatForDisplay(toolName, arguments);
        var approvalContext = new ToolApprovalContext(
            toolName.Value,
            displayText,
            allPatterns,
            [
                new ToolApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolApprovalOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolApprovalOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolApprovalOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
            ]);

        return ToolAccessDecision.RequiresApproval(approvalContext);
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
        => SecurityPolicyDefaults.TryParseAudience(context?.Audience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(context?.SessionId);

    private static ToolExecutionContext CreateContext(TrustAudience audience)
        => new(null, null) { Audience = audience.ToWireValue() };

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
    IReadOnlyList<string> UnapprovedPatterns,
    IReadOnlyList<ToolApprovalOption> Options);

public sealed record ToolApprovalOption(string Key, string Label);

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
