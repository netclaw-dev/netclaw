// -----------------------------------------------------------------------
// <copyright file="DaemonToolAuthorizationRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Registers the daemon's tool authorization components. The daemon and the
/// approval contract tests use the same registration, so the tests observe the
/// production composition.
/// </summary>
internal static class DaemonToolAuthorizationRegistrationExtensions
{
    /// <summary>
    /// Registers the hard-deny policy, the tool access policy, the approval
    /// store, and the approval service.
    /// </summary>
    /// <remarks>
    /// The caller supplies the protected-path policy and the safe-verb list.
    /// The daemon derives both from its Netclaw home and its host shell.
    /// </remarks>
    /// <returns>The tool access policy that the tool registry and the executor use.</returns>
    public static ToolAccessPolicy AddDaemonToolAuthorization(
        this IServiceCollection services,
        NetclawPaths paths,
        ShellExecutionEnvironment shellEnvironment,
        ToolConfig toolConfig,
        EffectivePolicyDefaults effectivePolicyDefaults,
        ToolPathPolicy toolPathPolicy,
        SafeVerbList safeVerbs,
        FeatureGates featureGates,
        TimeProvider timeProvider)
    {
        // Load operator-authored hard-deny overrides (additive only — see
        // HardDenyOverridesLoader). Missing file → empty list and only shipped
        // defaults apply. Malformed file → daemon refuses to start; the
        // loader throws InvalidDataException with operator-facing context so
        // the failure surfaces loudly rather than silently dropping rules.
        var hardDenyOverridesLoader = new HardDenyOverridesLoader();
        var hardDenyOverrides = hardDenyOverridesLoader.Load(paths.HardDenyOverridesPath);
        services.AddSingleton(hardDenyOverridesLoader);

        var shellCommandPolicy = new ShellCommandPolicy(
            shellEnvironment,
            toolConfig.HardDenyPatterns,
            hardDenyOverrides);
        services.AddSingleton(shellCommandPolicy);

        var fileApprovalMatcher = new FilePathApprovalMatcher(paths.ConfigDirectory);

        var toolAccessPolicy = new ToolAccessPolicy(
            paths,
            toolConfig,
            effectivePolicyDefaults,
            shellCommandPolicy,
            toolPathPolicy,
            fileApprovalMatcher,
            featureGates,
            safeVerbs);
        services.AddSingleton(toolAccessPolicy);

        var approvalShell = shellEnvironment.Grammar switch
        {
            ShellGrammar.Bash => ApprovalShell.Bash,
            ShellGrammar.PowerShell => ApprovalShell.PowerShell,
            _ => throw new InvalidOperationException("The native shell grammar is invalid.")
        };
        var toolApprovalStore = new ToolApprovalStore(
            paths.ToolApprovalsPath,
            ApprovalScopeFacts.Instance,
            timeProvider,
            new ApprovalStoreMigrationContext(approvalShell));
        services.AddSingleton(toolApprovalStore);
        services.AddSingleton<IToolApprovalService, AkkaToolApprovalService>();

        return toolAccessPolicy;
    }

    /// <summary>
    /// Registers the tool registry and the executor that authorizes and runs each tool call.
    /// </summary>
    public static IServiceCollection AddDaemonToolExecutor(
        this IServiceCollection services,
        ToolRegistry toolRegistry,
        ToolAccessPolicy toolAccessPolicy)
    {
        services.AddSingleton(toolRegistry);
        services.AddSingleton<IToolExecutor>(sp =>
            new DispatchingToolExecutor(
                toolRegistry,
                toolAccessPolicy,
                sp.GetService<IToolApprovalService>(),
                sp.GetRequiredService<ILogger<DispatchingToolExecutor>>()));
        return services;
    }
}
