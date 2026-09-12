// -----------------------------------------------------------------------
// <copyright file="ExposureModeValidationService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Linq;
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
    private readonly BootstrapDeviceSeeder? _bootstrapDeviceSeeder;

    public ExposureModeValidationService(
        DaemonConfig config,
        ILogger<ExposureModeValidationService> logger,
        IEnumerable<IRemoteAuthSchemeRegistration> remoteAuthSchemes,
        DeviceRegistry deviceRegistry,
        BootstrapDeviceSeeder bootstrapDeviceSeeder)
        : this(config, logger,
               processName => Process.GetProcessesByName(processName).Length > 0,
               remoteAuthSchemes,
               async ct => (await deviceRegistry.ListAsync(ct)).Count,
               bootstrapDeviceSeeder)
    {
    }

    // Internal constructor for unit testing: inject fake process detector, scheme list, and device counter.
    internal ExposureModeValidationService(
        DaemonConfig config,
        ILogger<ExposureModeValidationService> logger,
        Func<string, bool> processDetector,
        IEnumerable<IRemoteAuthSchemeRegistration>? remoteAuthSchemes = null,
        Func<CancellationToken, Task<int>>? deviceCounter = null,
        BootstrapDeviceSeeder? bootstrapDeviceSeeder = null)
    {
        _config = config;
        _logger = logger;
        _processDetector = processDetector;
        _remoteAuthSchemes = remoteAuthSchemes;
        _deviceCounter = deviceCounter;
        _bootstrapDeviceSeeder = bootstrapDeviceSeeder;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (DaemonExposureValidator.TryGetInvalidTrustedProxy(_config.TrustedProxies, out var invalidTrustedProxyError))
        {
            _logger.LogCritical(
                "Daemon startup aborted: {Message} Remediation: {Remediation}",
                invalidTrustedProxyError!,
                "Each Daemon.TrustedProxies entry must be a literal IP address or CIDR, for example '10.0.0.5' or '10.0.0.0/24'.");

            throw new InvalidOperationException(
                $"{invalidTrustedProxyError!} Each Daemon.TrustedProxies entry must be a literal IP address or CIDR, for example '10.0.0.5' or '10.0.0.0/24'.");
        }

        if (_config.ExposureMode == ExposureMode.Local)
        {
            if (DaemonExposureValidator.GetLoopbackViolationIssue(_config) is { } loopbackIssue)
            {
                _logger.LogCritical(
                    "Daemon startup aborted: {Message} Remediation: {Remediation}",
                    loopbackIssue.Message,
                    loopbackIssue.Remediation);

                throw new InvalidOperationException($"{loopbackIssue.Message} {loopbackIssue.Remediation}");
            }

            return;
        }

        if (DaemonExposureValidator.GetMissingRequiredProcessIssue(_config, _processDetector) is { } processIssue)
        {
            _logger.LogCritical(
                "Daemon startup aborted: {Message} Remediation: {Remediation}",
                processIssue.Message,
                processIssue.Remediation);

            throw new InvalidOperationException($"{processIssue.Message} {processIssue.Remediation}");
        }

        if (_bootstrapDeviceSeeder is not null)
            await _bootstrapDeviceSeeder.EnsureSeededAsync(_config, cancellationToken);

        var hasRemoteAuthenticationPath = false;

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

            hasRemoteAuthenticationPath = hasAlternativeRemoteAuthScheme || deviceCount > 0;

            var hasDeviceTokenScheme = _remoteAuthSchemes.Any(s => string.Equals(
                s.SchemeName,
                DeviceTokenAuthenticationHandler.SchemeName,
                StringComparison.Ordinal));

            // Wiring-integrity check for tunnel modes.
            //
            // Once LoopbackAuthenticationHandler is gated on RequiresRemoteAuthentication()
            // (audit finding 24), the device-bearer scheme is the ONLY path a remote
            // tunnel client can use to authenticate. If neither an alternative scheme nor
            // the device-bearer scheme is registered, the daemon would start cleanly but
            // be unreachable to any legitimate remote caller — fail loudly at startup
            // with an actionable wiring error rather than silently serving an
            // unauthenticatable surface.
            if (_config.ExposureMode.IsTunnelMode()
                && !hasAlternativeRemoteAuthScheme
                && !hasDeviceTokenScheme)
            {
                const string msg = "Tunnel exposure mode requires the device-bearer authentication scheme to be registered.";
                const string remediation = "This is an internal wiring error — the DeviceToken authentication scheme must be registered in DI for tunnel exposure modes (tailscale-serve, tailscale-funnel, cloudflare-tunnel).";
                _logger.LogCritical(
                    "Daemon startup aborted: {Message} Remediation: {Remediation}",
                    msg,
                    remediation);

                throw new InvalidOperationException($"{msg} {remediation}");
            }
        }

        foreach (var issue in DaemonExposureValidator.Validate(_config, hasRemoteAuthenticationPath)
                     .Where(static issue => !issue.IsTrustedProxyIssue))
        {
            _logger.LogCritical(
                "Daemon startup aborted: {Message} Remediation: {Remediation}",
                issue.Message,
                issue.Remediation);

            throw new InvalidOperationException($"{issue.Message} {issue.Remediation}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
