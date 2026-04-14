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

        if (effective.DeploymentPosture == DeploymentPosture.Personal
            && effective.ShellExecutionMode == ShellExecutionMode.HostAllowed)
        {
            warnings.Add("Personal posture with HostAllowed shell — full host access is enabled.");
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
}
