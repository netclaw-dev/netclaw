using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Routes <see cref="FunctionCallContent"/> to the correct tool by name via the <see cref="ToolRegistry"/>.
/// Logs every tool execution with name, duration, and result preview.
/// </summary>
public sealed class DispatchingToolExecutor : IToolExecutor
{
    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy _policy;
    private readonly ILogger _logger;

    public DispatchingToolExecutor(ToolRegistry registry, ILogger<DispatchingToolExecutor>? logger = null)
        : this(
            registry,
            new ToolAccessPolicy(
                new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)),
            logger)
    {
    }

    public DispatchingToolExecutor(ToolRegistry registry, ToolAccessPolicy policy, ILogger<DispatchingToolExecutor>? logger = null)
    {
        _registry = registry;
        _policy = policy;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        var tool = _registry.GetByName(toolCall.Name);
        if (tool is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            return $"Unknown tool: {toolCall.Name}";
        }

        var accessDecision = _policy.AuthorizeInvocation(tool, context);
        if (!accessDecision.Allowed)
        {
            _logger.LogWarning("Tool denied by policy: {ToolName} reason={Reason}", toolCall.Name, accessDecision.DenyReason);
            throw new ToolAccessDeniedException(accessDecision.DenyReason ?? "tool_denied");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = context is not null
                ? await tool.ExecuteAsync(toolCall.Arguments, context, ct)
                : await tool.ExecuteAsync(toolCall.Arguments, ct);

            sw.Stop();
            _logger.LogInformation(
                "Tool executed: {ToolName} ({Duration}ms, {ResultLength} chars)",
                toolCall.Name, sw.ElapsedMilliseconds, result.Length);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Tool execution failed: {ToolName} ({Duration}ms)",
                toolCall.Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
