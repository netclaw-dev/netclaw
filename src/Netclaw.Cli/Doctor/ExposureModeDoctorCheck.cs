// -----------------------------------------------------------------------
// <copyright file="ExposureModeDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Doctor check that validates daemon exposure mode configuration.
/// <list type="bullet">
/// <item>Warning: <see cref="ExposureMode.Local"/> with a non-loopback bind address — the
/// daemon is reachable beyond loopback without tunnel protection.</item>
/// <item>Error: non-local exposure mode declared but the required tunnel process is not
/// running.</item>
/// <item>Pass: local mode bound to loopback, or non-local mode with healthy tunnel
/// process.</item>
/// </list>
/// </summary>
public sealed class ExposureModeDoctorCheck : IDoctorCheck
{
    private const string CheckName = "exposure-mode";

    private readonly NetclawPaths _paths;
    private readonly Func<string, bool> _processDetector;

    public ExposureModeDoctorCheck(NetclawPaths paths)
        : this(paths, processName => Process.GetProcessesByName(processName).Length > 0)
    {
    }

    // Internal constructor for unit testing: inject a fake process detector.
    internal ExposureModeDoctorCheck(NetclawPaths paths, Func<string, bool> processDetector)
    {
        _paths = paths;
        _processDetector = processDetector;
    }

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(_paths);
        if (error is not null)
            return Task.FromResult(error);

        var daemonNode = root!["Daemon"] as JsonObject;

        var host = daemonNode?["Host"]?.GetValue<string>() ?? "127.0.0.1";
        var modeStr = daemonNode?["ExposureMode"]?.GetValue<string>() ?? "local";

        ExposureMode mode;
        try
        {
            mode = DaemonConfig.ParseExposureMode(modeStr);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                CheckName,
                $"Unknown ExposureMode value '{modeStr}' in Daemon section.",
                "Valid values: local, tailscale-serve, tailscale-funnel, cloudflare-tunnel."));
        }

        // Warning: local mode with non-loopback bind address — daemon exposed without tunnel.
        if (mode == ExposureMode.Local && !IsLoopback(host))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                CheckName,
                $"ExposureMode is 'local' but bind address '{host}' is not loopback. " +
                "The daemon is reachable beyond loopback without tunnel protection.",
                "Set ExposureMode to a tunnel mode (tailscale-serve, tailscale-funnel, " +
                "cloudflare-tunnel) or bind to 127.0.0.1 in netclaw.json."));
        }

        // Error: non-local mode but required tunnel process is not running.
        if (mode != ExposureMode.Local)
        {
            var requiredProcess = mode.GetRequiredProcessName();

            if (requiredProcess is not null && !_processDetector(requiredProcess))
            {
                var modeDisplay = mode.ToWireValue();
                return Task.FromResult(DoctorCheckResult.Error(
                    CheckName,
                    $"ExposureMode is '{modeDisplay}' but '{requiredProcess}' is not running.",
                    $"Start '{requiredProcess}' before starting Netclaw, " +
                    "or set ExposureMode to 'local' in netclaw.json."));
            }
        }

        var modeDescription = mode == ExposureMode.Local
            ? $"local (bound to {host})"
            : mode.ToWireValue();

        return Task.FromResult(DoctorCheckResult.Pass(
            CheckName,
            $"ExposureMode: {modeDescription}."));
    }

    private static bool IsLoopback(string host)
        => host is "127.0.0.1" or "::1" or "localhost";

}
