// -----------------------------------------------------------------------
// <copyright file="SubAgentTestScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

internal static class SubAgentTestScope
{
    public static ChildRunScope Create(
        TrustAudience audience = TrustAudience.Personal,
        string scopeId = "test-session/subagent/test/run",
        string? sessionDirectory = null,
        string? projectDirectory = null,
        string? inheritedCwd = null,
        IReadOnlyList<string>? recentFiles = null,
        ModelModality modelInputModalities = ModelModality.Text,
        IParentApprovalBridge? approvalBridge = null)
    {
        var workingContext = new WorkingContext
        {
            ProjectDirectory = projectDirectory,
            RecentFiles = [.. recentFiles ?? []]
        };

        var childScopeId = new SubAgentScopeId(scopeId);
        var parentStorage = SessionStoragePaths.CreateLegacy(
            sessionDirectory ?? Path.Combine(AppContext.BaseDirectory, "netclaw-test-session-workspace"),
            Path.Combine(AppContext.BaseDirectory, "netclaw-test-session-logs"),
            "test-session");
        ToolSessionScope sessionScope = new ToolSessionScope.Bound(scopeId, parentStorage);
        if (childScopeId.TryGetRunId(out var runId))
        {
            sessionScope = new ToolSessionScope.Bound(
                scopeId,
                parentStorage.ForChild(runId, childScopeId));
        }

        return new ChildRunScope
        {
            ScopeId = childScopeId,
            Authority = new ToolRunScope
            {
                Session = sessionScope,
                Audience = audience,
                InlineOutputBudget = InlineOutputBudget.Default,
                ModelInputModalities = modelInputModalities,
                InteractiveApproval = approvalBridge is null
                    ? new InteractiveApprovalCapability.Unavailable()
                    : new InteractiveApprovalCapability.Available(approvalBridge),
                ProjectDirectory = projectDirectory,
                InheritedCwd = inheritedCwd,
                RecentFiles = recentFiles ?? []
            },
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = audience == TrustAudience.Public ? WorkingContext.Empty : workingContext,
                Git = new GitWorkingContextInspection.Skipped()
            }
        };
    }
}
