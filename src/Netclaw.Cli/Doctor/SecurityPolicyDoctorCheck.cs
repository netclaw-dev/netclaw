// -----------------------------------------------------------------------
// <copyright file="SecurityPolicyDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class SecurityPolicyDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (error is not null)
            return Task.FromResult(error);

        if (root is null)
            return Task.FromResult(DoctorCheckResult.Error(
                "Security Policy",
                "Config file is missing; security policy cannot be evaluated.",
                "Run `netclaw init` to scaffold a baseline config with security defaults."));

        if (root["Security"] is not JsonObject securityObject)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Security Policy",
                "Security section is missing; strict fallback defaults are active (Public posture, shell disabled).",
                "Add a Security section with DeploymentPosture or run `netclaw init` again."));
        }

        SecurityPolicyConfig config;
        try
        {
            config = JsonSerializer.Deserialize<SecurityPolicyConfig>(securityObject, JsonDefaults.ConfigRead)
                     ?? new SecurityPolicyConfig();
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Security Policy",
                $"Failed to parse Security configuration: {ex.Message}",
                "Fix Security section values or rerun `netclaw init`."));
        }

        var effective = SecurityPolicyDefaults.Resolve(config);

        if (!config.DeploymentPosture.HasValue && !config.StrictDefaults)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Security Policy",
                "DeploymentPosture is null with StrictDefaults disabled — this silently assumes Personal posture with full access.",
                "Set an explicit DeploymentPosture value or enable StrictDefaults."));
        }

        var warnings = new List<string>();

        if (effective.UsedStrictFallback && !config.DeploymentPosture.HasValue)
        {
            warnings.Add("DeploymentPosture not set; strict fallback resolved to Public.");
        }

        // Only warn about Personal + HostAllowed when the posture is implicit
        // (resolved from fallback defaults). If the user explicitly set
        // DeploymentPosture = Personal, they chose this intentionally.
        if (effective.DeploymentPosture == DeploymentPosture.Personal
            && effective.ShellExecutionMode == ShellExecutionMode.HostAllowed
            && !config.DeploymentPosture.HasValue)
        {
            warnings.Add("Personal posture with HostAllowed shell — full host access is enabled.");
        }

        // The daemon reads Tools.ShellMode before Security.ShellExecutionMode (ToolAccessPolicy.ResolveShellMode).
        // A split between the two means the shell limit and the posture came from different edits.
        ShellExecutionMode? toolsShellMode;
        try
        {
            toolsShellMode = ReadToolsShellMode(root);
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Security Policy",
                $"Failed to parse Tools.ShellMode: {ex.Message}",
                "Fix the Tools.ShellMode value or remove it."));
        }

        if (toolsShellMode.HasValue && toolsShellMode.Value != effective.ShellExecutionMode)
        {
            warnings.Add(
                $"Tools.ShellMode is {toolsShellMode.Value} but Security.ShellExecutionMode is {effective.ShellExecutionMode}. The daemon uses Tools.ShellMode.");
        }

        if (warnings.Count > 0)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Security Policy",
                string.Join(" ", warnings),
                "Review deployment posture and shell mode. Set explicit values to suppress this warning."));
        }

        return Task.FromResult(DoctorCheckResult.Pass(
            "Security Policy",
            $"Deployment posture: {effective.DeploymentPosture}, Shell: {effective.ShellExecutionMode}."));
    }

    private static ShellExecutionMode? ReadToolsShellMode(JsonObject root)
        => root["Tools"] is JsonObject tools
            ? JsonSerializer.Deserialize<ToolsShellModeSection>(tools, JsonDefaults.ConfigRead)?.ShellMode
            : null;

    private sealed record ToolsShellModeSection(ShellExecutionMode? ShellMode);
}
