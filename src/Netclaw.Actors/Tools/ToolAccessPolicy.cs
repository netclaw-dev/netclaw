using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

public sealed class ToolAccessPolicy
{
    private readonly ToolConfig _toolConfig;
    private readonly EffectivePolicyDefaults _defaults;
    private readonly ToolAudienceProfileResolver _profileResolver;

    public ToolAccessPolicy(ToolConfig toolConfig, EffectivePolicyDefaults defaults)
    {
        _toolConfig = toolConfig;
        _defaults = defaults;
        _profileResolver = new ToolAudienceProfileResolver(toolConfig);
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
    {
        return IsToolExposed(registration.Tool, ResolveAudience(trustContext));
    }

    public bool IsToolExposed(INetclawTool tool, EffectiveTrustContext? trustContext)
        => IsToolExposed(tool, ResolveAudience(trustContext));

    public bool IsToolExposed(INetclawTool tool, ToolExecutionContext? context)
        => IsToolExposed(tool, ResolveAudience(context));

    private bool IsToolExposed(INetclawTool tool, TrustAudience audience)
    {
        if (tool is McpToolAdapter mcp)
            return _profileResolver.IsMcpServerAllowed(mcp.ServerName, audience)
                && IsMcpToolExposed(mcp.CapabilityClass, audience);

        if (!_profileResolver.IsToolAllowed(tool.Name, CreateContext(audience)))
            return false;

        if (IsShellTool(tool))
            return ResolveShellMode() == ShellExecutionMode.HostAllowed && audience == TrustAudience.Personal;

        return true;
    }

    public ToolAccessDecision AuthorizeInvocation(INetclawTool tool, ToolExecutionContext? context)
    {
        if (tool is McpToolAdapter mcp)
        {
            var audience = ResolveAudience(context);
            if (!_profileResolver.IsMcpServerAllowed(mcp.ServerName, context))
                return ToolAccessDecision.Deny("mcp_server_not_allowed_for_audience_profile");

            return IsMcpToolExposed(mcp.CapabilityClass, audience)
                ? ToolAccessDecision.Allow()
                : ToolAccessDecision.Deny("mcp_capability_denied_for_audience");
        }

        if (!_profileResolver.IsToolAllowed(tool.Name, context))
            return ToolAccessDecision.Deny("tool_not_allowed_for_audience_profile");

        if (!IsShellTool(tool))
            return ToolAccessDecision.Allow();

        var shellMode = ResolveShellMode();
        if (shellMode == ShellExecutionMode.Off)
            return ToolAccessDecision.Deny("shell_disabled");

        if (shellMode == ShellExecutionMode.SandboxOnly)
            return ToolAccessDecision.Deny("shell_requires_sandbox_backend");

        var shellAudience = ResolveAudience(context);
        return shellAudience == TrustAudience.Personal
            ? ToolAccessDecision.Allow()
            : ToolAccessDecision.Deny("shell_requires_personal_context");
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
        => string.Equals(tool.Name, "shell_execute", StringComparison.Ordinal);

    private static bool IsMcpToolExposed(McpCapabilityClass capabilityClass, TrustAudience audience)
        => capabilityClass switch
        {
            McpCapabilityClass.Information => true,
            McpCapabilityClass.MemorySafe => audience is TrustAudience.Team or TrustAudience.Personal,
            McpCapabilityClass.SensitiveRead => audience == TrustAudience.Personal,
            McpCapabilityClass.PublishExternal => audience == TrustAudience.Personal,
            McpCapabilityClass.HighImpact => audience == TrustAudience.Personal,
            _ => audience == TrustAudience.Personal
        };

    private static string? GetToolName(AITool tool)
        => tool is AIFunction function ? function.Name : null;
}

public sealed record ToolAccessDecision(bool Allowed, string? DenyReason = null)
{
    public static ToolAccessDecision Allow() => new(true);

    public static ToolAccessDecision Deny(string reason) => new(false, reason);
}

public sealed class ToolAccessDeniedException : InvalidOperationException
{
    public ToolAccessDeniedException(string denyReason)
        : base(denyReason)
    {
        DenyReason = denyReason;
    }

    public string DenyReason { get; }
}
