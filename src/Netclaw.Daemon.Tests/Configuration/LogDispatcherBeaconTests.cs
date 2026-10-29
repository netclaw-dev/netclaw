// -----------------------------------------------------------------------
// <copyright file="LogDispatcherBeaconTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class LogDispatcherBeaconTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    [Fact]
    public void The_dispatcher_failure_beacon_masks_a_secret_and_stays_in_one_record()
    {
        var path = Path.Join(_directory.Path, "daemon.log");
        var failed = Task.FromException<IActorRef>(
            new InvalidOperationException("lookup failed with password=hunter2\n17:00:00.000 [INF] fake"));

        // Dispose waits for the writer thread before the test reads the file.
        using (var provider = new RollingFileLoggerProvider(path, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-29T17:00:00Z"))))
        {
            // The task has already faulted, so resolution fails synchronously inside this call
            // and queues the beacon before it returns.
            provider.AttachSessionDispatcher(failed);
        }

        var text = File.ReadAllText(Directory.GetFiles(_directory.Path, "daemon-*.log").Single());
        Assert.Contains("password=***REDACTED***", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("\n  17:00:00.000 [INF] fake", text.Replace("\r", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    public void Dispose() => _directory.Dispose();
}
