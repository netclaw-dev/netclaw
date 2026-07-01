// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _basePath = Path.Join(Path.GetTempPath(), $"netclaw-rolling-logger-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Writes_log_lines_to_the_daily_rolling_daemon_log()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Join(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);

        // Dispose drains the writer thread, so the line is flushed by the time we read.
        using (var provider = new RollingFileLoggerProvider(daemonLogPath, time))
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            logger.LogInformation("hello daemon log {SessionId}", "channel/thread");
        }

        // No dispatcher was attached, so per-session routing is off and even a session-tagged
        // line falls back to daemon.log. (When routing IS attached the {SessionId} line is
        // partitioned to session.log — see RollingFileLoggerPartitionTests.)
        var daemonLog = Directory.GetFiles(Path.Join(_basePath, "logs"), "daemon-*.log").Single();
        var text = await File.ReadAllTextAsync(daemonLog, TestContext.Current.CancellationToken);
        Assert.Contains("hello daemon log", text, StringComparison.Ordinal);
        Assert.Contains("Netclaw.Tests", text, StringComparison.Ordinal);
        Assert.Contains("channel/thread", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_basePath))
                Directory.Delete(_basePath, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[RollingFileLoggerProviderTests] cleanup failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"[RollingFileLoggerProviderTests] cleanup failed: {ex.Message}");
        }
    }
}
