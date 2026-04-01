using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Validates tunnel prerequisites at daemon startup based on the configured
/// <see cref="ExposureMode"/>. If the declared exposure mode requires a tunnel
/// process (e.g., <c>tailscaled</c> or <c>cloudflared</c>) and that process is
/// not running, startup is aborted with a descriptive error.
///
/// <para>
/// <see cref="ExposureMode.Local"/> skips all checks — no external process is required
/// when the daemon binds loopback only.
/// </para>
/// </summary>
internal sealed class ExposureModeValidationService : IHostedService
{
    private readonly DaemonConfig _config;
    private readonly ILogger<ExposureModeValidationService> _logger;
    private readonly Func<string, bool> _processDetector;

    public ExposureModeValidationService(
        DaemonConfig config,
        ILogger<ExposureModeValidationService> logger)
        : this(config, logger, processName => Process.GetProcessesByName(processName).Length > 0)
    {
    }

    // Internal constructor for unit testing: inject a fake process detector.
    internal ExposureModeValidationService(
        DaemonConfig config,
        ILogger<ExposureModeValidationService> logger,
        Func<string, bool> processDetector)
    {
        _config = config;
        _logger = logger;
        _processDetector = processDetector;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.ExposureMode == ExposureMode.Local)
            return Task.CompletedTask;

        var requiredProcess = _config.ExposureMode switch
        {
            ExposureMode.TailscaleServe or ExposureMode.TailscaleFunnel => "tailscaled",
            ExposureMode.CloudflareTunnel => "cloudflared",
            _ => throw new InvalidOperationException(
                $"Unknown ExposureMode: {_config.ExposureMode}. " +
                "Cannot determine tunnel prerequisite.")
        };

        if (_processDetector(requiredProcess))
            return Task.CompletedTask;

        var modeWireValue = _config.ExposureMode switch
        {
            ExposureMode.TailscaleServe => "tailscale-serve",
            ExposureMode.TailscaleFunnel => "tailscale-funnel",
            ExposureMode.CloudflareTunnel => "cloudflare-tunnel",
            _ => throw new ArgumentOutOfRangeException(nameof(_config.ExposureMode), _config.ExposureMode, $"Unknown ExposureMode value: {_config.ExposureMode}")
        };

        _logger.LogCritical(
            "Daemon startup aborted: ExposureMode is '{Mode}' but the required tunnel process " +
            "'{Process}' is not running. Start '{Process}' before starting Netclaw, or set " +
            "ExposureMode to 'local' in netclaw.json.",
            modeWireValue, requiredProcess, requiredProcess);

        throw new InvalidOperationException(
            $"Tunnel prerequisite not met: ExposureMode='{modeWireValue}' requires " +
            $"'{requiredProcess}' to be running. Start '{requiredProcess}' or set ExposureMode " +
            "to 'local' in netclaw.json.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
