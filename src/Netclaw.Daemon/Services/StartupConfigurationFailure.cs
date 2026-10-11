// -----------------------------------------------------------------------
// <copyright file="StartupConfigurationFailure.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Reports an invalid operator configuration that stops startup. The one message goes to stderr and
/// to daemon.log. It does not go through <see cref="DaemonCrashMonitor"/>: the daemon is not
/// crashing, and no stack trace or crash log helps the operator fix a config file.
/// </summary>
internal static class StartupConfigurationFailure
{
    public static void Report(NetclawPaths paths, string message, TextWriter stderr)
    {
        stderr.WriteLine($"error: {message}");

        // Logging is not configured this early in startup, so write the line directly. Dispose
        // drains the writer thread.
        using var provider = new RollingFileLoggerProvider(paths.DaemonLogPath);
        provider.CreateLogger("Netclaw.Startup").LogError("Daemon startup stopped: {Message}", message);
    }
}

/// <summary>
/// Holds the reason the config watcher refused the file on disk, or null when the running daemon
/// uses the file as it is. the status endpoint reads it so that <c>netclaw status</c>
/// can say that the running configuration differs from the file.
/// </summary>
public sealed class RejectedConfigState
{
    private volatile string? _reason;

    public string? Reason => _reason;

    public void Reject(string reason) => _reason = reason;

    public void Clear() => _reason = null;
}
