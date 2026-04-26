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
    private readonly IToolApprovalMatcher _fileApprovalMatcher;

    public ToolAccessPolicy(
        ToolConfig toolConfig,
        EffectivePolicyDefaults defaults,
        ShellCommandPolicy? shellCommandPolicy = null,
        IToolApprovalMatcher? fileApprovalMatcher = null,
        ToolPathPolicy? toolPathPolicy = null)
    {
        _toolConfig = toolConfig;
        _defaults = defaults;
        _profileResolver = new ToolAudienceProfileResolver(toolConfig);
        _shellCommandPolicy = shellCommandPolicy;
        _toolPathPolicy = toolPathPolicy;
        _fileApprovalMatcher = fileApprovalMatcher ?? DefaultApprovalMatcher.Instance;
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

        return CheckApprovalGate(toolName, context, arguments, ShellApprovalMatcher.Instance);
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

        if (context?.SupportsInteractiveApproval == false)
            return ToolAccessDecision.Deny("channel_does_not_support_approval");

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

    private static string? GetToolName(AITool tool)
        => tool is AIFunction function ? function.Name : null;
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
