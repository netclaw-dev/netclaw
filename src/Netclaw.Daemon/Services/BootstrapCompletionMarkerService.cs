// -----------------------------------------------------------------------
// <copyright file="BootstrapCompletionMarkerService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Marks daemon-owned bootstrap seeding as complete after the first successful
/// non-local daemon start.
/// </summary>
internal sealed class BootstrapCompletionMarkerService : IHostedService
{
    private readonly DaemonConfig _daemonConfig;
    private readonly BootstrapDeviceSeeder _bootstrapDeviceSeeder;
    private readonly IHostApplicationLifetime _applicationLifetime;

    public BootstrapCompletionMarkerService(
        DaemonConfig daemonConfig,
        BootstrapDeviceSeeder bootstrapDeviceSeeder,
        IHostApplicationLifetime applicationLifetime)
    {
        _daemonConfig = daemonConfig;
        _bootstrapDeviceSeeder = bootstrapDeviceSeeder;
        _applicationLifetime = applicationLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_daemonConfig.ExposureMode.RequiresRemoteAuthentication())
            _applicationLifetime.ApplicationStarted.Register(_bootstrapDeviceSeeder.MarkCompleted);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
