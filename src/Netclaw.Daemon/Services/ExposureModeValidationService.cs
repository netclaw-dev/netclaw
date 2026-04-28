// -----------------------------------------------------------------------
// <copyright file="ExposureModeValidationService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Validates tunnel prerequisites at daemon startup based on the configured
/// <see cref="ExposureMode"/>. If the declared exposure mode requires a tunnel
/// process (e.g., <c>tailscaled</c> or <c>cloudflared</c>) and that process is
/// not running, startup is aborted with a descriptive error.
///
/// <para>
/// For non-local exposure modes, also verifies that at least one remote authentication
/// scheme is registered OR that at least one device is already paired. If neither is
/// true, startup is aborted — there would be no way for any remote client to authenticate.
/// </para>
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
    private readonly IEnumerable<IRemoteAuthSchemeRegistration>? _remoteAuthSchemes;
    private readonly Func<CancellationToken, Task<int>>? _deviceCounter;

    public ExposureModeValidationService(
        DaemonConfig config,
        ILogger<ExposureModeValidationService> logger,
        IEnumerable<IRemoteAuthSchemeRegistration> remoteAuthSchemes,
        DeviceRegistry deviceRegistry)
        : this(config, logger,
               processName => Process.GetProcessesByName(processName).Length > 0,
               remoteAuthSchemes,
               async ct => (await deviceRegistry.ListAsync(ct)).Count)
    {
    }

    // Internal constructor for unit testing: inject fake process detector, scheme list, and device counter.
    internal ExposureModeValidationService(
        DaemonConfig config,
        ILogger<ExposureModeValidationService> logger,
        Func<string, bool> processDetector,
        IEnumerable<IRemoteAuthSchemeRegistration>? remoteAuthSchemes = null,
        Func<CancellationToken, Task<int>>? deviceCounter = null)
    {
        _config = config;
        _logger = logger;
        _processDetector = processDetector;
        _remoteAuthSchemes = remoteAuthSchemes;
        _deviceCounter = deviceCounter;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.ExposureMode == ExposureMode.Local)
            return;

        var requiredProcess = _config.ExposureMode.GetRequiredProcessName()
            ?? throw new InvalidOperationException(
                $"Unknown ExposureMode: {_config.ExposureMode}. " +
                "Cannot determine tunnel prerequisite.");

        if (!_processDetector(requiredProcess))
        {
            var modeWireValue = _config.ExposureMode.ToWireValue();

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

        // Remote auth guard: only applies when scheme registrations are explicitly provided
        // (i.e., the production DI path). Tests that don't inject this skip the check.
        if (_remoteAuthSchemes is not null)
        {
            var hasAlternativeRemoteAuthScheme = _remoteAuthSchemes.Any(s =>
                !string.Equals(
                    s.SchemeName,
                    DeviceTokenAuthenticationHandler.SchemeName,
                    StringComparison.Ordinal));

            var deviceCount = _deviceCounter is not null
                ? await _deviceCounter(cancellationToken)
                : 0;

            if (!hasAlternativeRemoteAuthScheme && deviceCount == 0)
            {
                var modeWireValue = _config.ExposureMode.ToWireValue();

                _logger.LogCritical(
                    "Daemon startup aborted: ExposureMode is '{Mode}' but no paired devices exist " +
                    "and no alternative remote authentication scheme is configured. Pair a device " +
                    "with 'netclaw daemon pair' or configure another remote auth scheme before " +
                    "starting Netclaw.",
                    modeWireValue);

                throw new InvalidOperationException(
                    $"No remote authentication available: ExposureMode='{modeWireValue}' requires " +
                    "either at least one paired device or an alternative remote auth scheme. " +
                    "Run 'netclaw daemon pair' to pair a device, or check your auth configuration.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
