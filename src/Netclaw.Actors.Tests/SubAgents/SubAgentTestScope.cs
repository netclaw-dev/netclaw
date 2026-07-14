// -----------------------------------------------------------------------
// <copyright file="SubAgentTestScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
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

        return new ChildRunScope
        {
            ScopeId = new SubAgentScopeId(scopeId),
            Authority = new ToolRunScope
            {
                Session = new ToolSessionScope.Bound(scopeId, sessionDirectory),
                Audience = audience,
                InlineOutputBudget = InlineOutputBudget.Default,
                ModelInputModalities = modelInputModalities,
                ApprovalBridge = approvalBridge,
                ProjectDirectory = projectDirectory,
                InheritedCwd = inheritedCwd,
                RecentFiles = recentFiles ?? [],
                SupportsInteractiveApproval = approvalBridge is not null
            },
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = audience == TrustAudience.Public ? WorkingContext.Empty : workingContext,
                Git = new GitWorkingContextInspection.Skipped()
            }
        };
    }
}
