// -----------------------------------------------------------------------
// <copyright file="ShellPolicyTestExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Policy-level views for unit tests of the <see cref="ToolAccessPolicy"/> rules.
/// Each view applies the rules in the authorizer's order and stops before the
/// grant lookup, so a test can assert the screen or the consent request alone.
/// Production asks <see cref="ToolAuthorizer"/> for the whole decision.
/// </summary>
internal static class ShellPolicyTestExtensions
{
    /// <summary>The decision of a call that is not a shell call, before the grant lookup.</summary>
    internal static ToolAuthorizationDecision AuthorizeInvocation(
        this ToolAccessPolicy policy,
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments = null)
    {
        if (string.Equals(tool.Name, ShellTool.ToolName, StringComparison.Ordinal))
            throw new InvalidOperationException("Shell commands require the shell rules of the authorizer.");

        if (policy.AdmitAudience(tool, context) is { } audienceDenial)
            return audienceDenial;

        if (tool is not McpToolAdapter)
        {
            if (string.Equals(tool.Name, CheckBackgroundJobTool.ToolName, StringComparison.Ordinal))
                return policy.AuthorizeBackgroundJobControl(context);

            if (policy.PreflightStructuredPathAccess(tool, context.Invocation, arguments) is { } pathDenial)
                return pathDenial;
        }

        var toolName = new ToolName(tool.Name);
        var approvalArguments = ToolAccessPolicy.GetApprovalArguments(tool, arguments);
        var matcher = policy.SelectApprovalMatcher(tool);
        var mode = policy.GetApprovalMode(toolName, context, approvalArguments, matcher);
        return ToolAccessPolicy.ScreenApprovalModeDenial(mode)
            ?? (mode == ToolApprovalMode.Auto
                ? ToolAuthorizationDecision.Allow(ToolAllowReason.PolicyAuto)
                : policy.BuildNonShellConsentRequest(toolName, context, approvalArguments, matcher));
    }

    /// <summary>The decision of a shell call after the screens and the consent mode, before coverage.</summary>
    internal static ToolAuthorizationDecision GetShellPreflightDecision(
        this ToolAccessPolicy policy,
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments = null)
        => policy.AuthorizeShellPreflight(tool, context, arguments).GetDecision();

    internal static ToolAuthorizationDecision GetDecision(this ShellPolicyPreflightResult result)
        => result switch
        {
            ShellPolicyPreflightResult.Complete complete => complete.Decision,
            ShellPolicyPreflightResult.Continue continuation =>
                ToolAuthorizationDecision.RequiresApproval(continuation.ApprovalContext),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

    /// <summary>
    /// The shell screens and the consent mode, in the authorizer's order, as
    /// the preflight result that advice and coverage selection read.
    /// </summary>
    internal static ShellPolicyPreflightResult AuthorizeShellPreflight(
        this ToolAccessPolicy policy,
        INetclawTool tool,
        ToolExecutionContext context,
        IDictionary<string, object?>? arguments = null)
    {
        if (!string.Equals(tool.Name, ShellTool.ToolName, StringComparison.Ordinal))
            throw new ArgumentException("Shell preflight requires the shell tool.", nameof(tool));

        static ShellPolicyPreflightResult Stop(ToolAuthorizationDecision decision)
            => ToolAccessPolicy.CompleteShellPreflight(decision, analysis: null, directoryScopes: null);

        var toolName = new ToolName(tool.Name);
        if ((policy.AdmitAudience(tool, context) ?? policy.EvaluateShellCapability(context.Invocation)) is { } admission)
            return Stop(admission);

        var command = ToolAccessPolicy.ExtractShellCommand(arguments);
        var workingDirectory = ToolAccessPolicy.ResolveShellWorkingDirectory(context, arguments);
        var analysis = command is null ? null : policy.ShellCommandPolicy.Analyze(command, workingDirectory);
        if (analysis is not null
            && (policy.ScreenHardDeny(analysis) ?? policy.ScreenProtectedShellText(analysis)) is { } prohibition)
        {
            return Stop(prohibition);
        }

        if (ToolAccessPolicy.ScreenShellWorkingDirectory(workingDirectory) is { } invalidWorkingDirectory)
            return Stop(invalidWorkingDirectory);

        var approval = analysis is null ? null : policy.AnalyzeShellApproval(toolName, arguments, workingDirectory, analysis);
        BashDirectoryScopeProjection? directoryScopes = null;
        if (analysis is not null && approval is not null
            && policy.TryProveDirectoryScopes(analysis, approval, out var proof))
        {
            if (policy.ScreenDirectoryScopes(proof, context) is { } scopedDenial)
                return Stop(scopedDenial);

            approval = ToolAccessPolicy.WithDirectoryScopes(approval, proof);
            directoryScopes = proof;
        }
        else if (analysis is not null && approval is not null
                 && policy.TryProjectLiteralTwins(analysis, out var twins))
        {
            if (policy.ScreenLiteralTwins(twins, context) is { } twinDenial)
                return Stop(twinDenial);

            approval = ToolAccessPolicy.WithLiteralTwins(approval, twins);
        }

        if (analysis is not null
            && policy.ScreenShellTrustZone(analysis, workingDirectory, context) is { } fileDenial)
        {
            return Stop(fileDenial);
        }

        var mode = policy.GetShellApprovalMode(toolName, context, arguments, analysis);
        if (ToolAccessPolicy.ScreenApprovalModeDenial(mode) is { } modeDenial)
            return Stop(modeDenial);

        return ToolAccessPolicy.CompleteShellPreflight(
            policy.AuthorizeShellApproval(toolName, context, arguments, mode, approval, workingDirectory),
            analysis,
            directoryScopes);
    }
}

/// <summary>
/// Flat views of <see cref="AuthorizationDecision"/> for test assertions, with
/// the names of the rule decision type.
/// </summary>
internal static class AuthorizationDecisionTestExtensions
{
    extension(AuthorizationDecision decision)
    {
        public ToolAuthorizationOutcome Outcome => decision switch
        {
            AuthorizationDecision.Allowed => ToolAuthorizationOutcome.Allowed,
            AuthorizationDecision.NeedsConsent => ToolAuthorizationOutcome.RequiresApproval,
            AuthorizationDecision.CorrectionRequired => ToolAuthorizationOutcome.RequiresAgentCorrection,
            AuthorizationDecision.Denied => ToolAuthorizationOutcome.Denied,
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown authorization decision.")
        };

        public bool NeedsApproval => decision is AuthorizationDecision.NeedsConsent;

        public ToolAllowReason? AllowReason => (decision as AuthorizationDecision.Allowed)?.Reason;

        public string? DenyReason => (decision as AuthorizationDecision.Denied)?.Reason;

        public string? DenyMessage => (decision as AuthorizationDecision.Denied)?.Message;

        public ToolApprovalContext? ApprovalContext => (decision as AuthorizationDecision.NeedsConsent)?.Request;

        public ToolCorrectionCollection? AgentCorrections => (decision as AuthorizationDecision.CorrectionRequired)?.Corrections;

        public ToolCorrection? AgentCorrection => decision.AgentCorrections switch
        {
            null => null,
            { Items.Count: 1 } corrections => corrections.Items[0],
            _ => throw new InvalidOperationException("A scalar correction consumer received multiple corrections.")
        };

        public IReadOnlyList<ToolApprovalMatch> ApprovalMatches => decision.Matches;

        public ShellPolicyDecisionTrace ShellPolicyTrace => decision.Trace;
    }
}
