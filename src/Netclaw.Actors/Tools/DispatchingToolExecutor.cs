// -----------------------------------------------------------------------
// <copyright file="DispatchingToolExecutor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Security;
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
    private readonly IToolApprovalService? _approvalService;
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
                UsedStrictFallback: false),
                new ShellCommandPolicy()),
            approvalService: null,
            logger)
    {
    }

    public DispatchingToolExecutor(ToolRegistry registry, ToolAccessPolicy policy,
        IToolApprovalService? approvalService = null, ILogger<DispatchingToolExecutor>? logger = null)
    {
        _registry = registry;
        _policy = policy;
        _approvalService = approvalService;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        if (_registry.GetByName(toolCall.Name) is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            return $"Unknown tool: {toolCall.Name}";
        }

        var tool = await AuthorizeCoreAsync(toolCall, context, ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = context is not null
                ? await tool.ExecuteAsync(toolCall.Arguments, context, ct)
                : await tool.ExecuteAsync(toolCall.Arguments, ct);

            result = SecretOutputRedactor.Redact(result);

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

    public async Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        _ = await AuthorizeCoreAsync(toolCall, context, ct);
    }

    private async Task<INetclawTool> AuthorizeCoreAsync(FunctionCallContent toolCall, ToolExecutionContext? context, CancellationToken ct)
    {
        var tool = _registry.GetByName(toolCall.Name);
        if (tool is null)
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            throw new ToolAccessDeniedException("tool_not_found");
        }

        var accessDecision = _policy.AuthorizeInvocation(tool, context, toolCall.Arguments);

        if (accessDecision.NeedsApproval && _approvalService is not null)
        {
            var approvalContext = accessDecision.ApprovalContext
                ?? throw new InvalidOperationException("Approval decision missing approval context.");
            var audience = SecurityPolicyDefaults.TryParseAudience(context?.Audience, out var parsed)
                ? parsed
                : SecurityPolicyDefaults.ResolveAudienceFromSessionId(context?.SessionId);
            var unapproved = await _approvalService.GetUnapprovedPatternsAsync(
                context?.SessionId,
                audience,
                new ToolName(toolCall.Name),
                approvalContext.UnapprovedPatterns,
                ct);

            accessDecision = unapproved.Count == 0
                ? ToolAccessDecision.Allow()
                : ToolAccessDecision.RequiresApproval(new ToolApprovalContext(
                    approvalContext.ToolName,
                    approvalContext.DisplayText,
                    unapproved,
                    approvalContext.Options));
        }

        if (accessDecision.NeedsApproval
            && context is not null
            && IsOneTimeApprovalSatisfied(context, toolCall, accessDecision.ApprovalContext))
        {
            _logger.LogInformation(
                "Applying one-time approval bypass for tool {ToolName} in session {SessionId}",
                toolCall.Name,
                context.SessionId ?? "unknown");
            accessDecision = ToolAccessDecision.Allow();
        }

        if (accessDecision.NeedsApproval)
        {
            _logger.LogInformation("Tool requires approval: {ToolName}", toolCall.Name);
            throw new ToolApprovalRequiredException(accessDecision.ApprovalContext!);
        }

        if (!accessDecision.Allowed)
        {
            _logger.LogWarning("Tool denied by policy: {ToolName} reason={Reason}", toolCall.Name, accessDecision.DenyReason);
            throw new ToolAccessDeniedException(accessDecision.DenyReason ?? "tool_denied");
        }

        return tool;
    }

    private static bool IsOneTimeApprovalSatisfied(
        ToolExecutionContext context,
        FunctionCallContent toolCall,
        ToolApprovalContext? approvalContext)
    {
        if (approvalContext is null)
            return false;

        if (context.OneTimeApprovedPatterns.Count == 0)
            return false;

        if (approvalContext.UnapprovedPatterns.Count == 0)
            return false;

        if (!string.Equals(context.OneTimeApprovedToolName, toolCall.Name, StringComparison.Ordinal))
            return false;

        return approvalContext.UnapprovedPatterns.All(pattern => context.OneTimeApprovedPatterns.Contains(pattern));
    }
}
