using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

public sealed class ToolAccessPolicy
{
    private readonly ToolConfig _toolConfig;
    private readonly EffectivePolicyDefaults _defaults;

    public ToolAccessPolicy(ToolConfig toolConfig, EffectivePolicyDefaults defaults)
    {
        _toolConfig = toolConfig;
        _defaults = defaults;
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

    public bool IsToolExposed(ToolRegistration registration, EffectiveTrustContext? trustContext)
    {
        if (!IsShellTool(registration))
            return true;

        return ResolveShellMode() == ShellExecutionMode.HostAllowed
               && ResolveAudience(trustContext) == TrustAudience.Personal;
    }

    public ToolAccessDecision AuthorizeInvocation(INetclawTool tool, ToolExecutionContext? context)
    {
        if (!IsShellTool(tool))
            return ToolAccessDecision.Allow();

        var shellMode = ResolveShellMode();
        if (shellMode == ShellExecutionMode.Off)
            return ToolAccessDecision.Deny("shell_disabled");

        if (shellMode == ShellExecutionMode.SandboxOnly)
            return ToolAccessDecision.Deny("shell_requires_sandbox_backend");

        var audience = ResolveAudience(context);
        return audience == TrustAudience.Personal
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

    private static bool IsShellTool(ToolRegistration registration)
        => registration.GrantCategory == "shell" || IsShellTool(registration.Tool);

    private static bool IsShellTool(INetclawTool tool)
        => string.Equals(tool.Name, "shell_execute", StringComparison.Ordinal);

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
