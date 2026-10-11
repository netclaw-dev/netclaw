// -----------------------------------------------------------------------
// <copyright file="DaemonLogRetentionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class DaemonLogRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-20T12:00:00Z");

    private readonly string _root = Path.Join(Path.GetTempPath(), $"netclaw-log-retention-tests-{Guid.NewGuid():N}");
    private string LogsDir => Path.Join(_root, "logs");

    public DaemonLogRetentionTests() => Directory.CreateDirectory(LogsDir);

    [Fact]
    public void Deletes_daemon_and_crash_logs_older_than_the_limit_and_keeps_the_rest()
    {
        // Cutoff is 2026-05-06 (today minus 14 days): that day stays, the day before goes.
        var old = new[] { "daemon-2026-05-05.log", "daemon-2026-01-01.log", "crash-20260505-235959.log", "crash-20260101-000000-4242-123-1.log", "crash-20260102-000000-4242-0123456789abcdef0123456789abcdef.log" };
        var kept = new[] { "daemon-2026-05-06.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log", "crash-20260506-000000.log", "crash-20260519-120000.log", "crash-20260520-110000.log" };
        Touch(old);
        Touch(kept);

        var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal(old.Length, deleted);
        Assert.Equal(0, failed);
        Assert.All(old, f => Assert.False(File.Exists(Path.Join(LogsDir, f)), f));
        Assert.All(kept, f => Assert.True(File.Exists(Path.Join(LogsDir, f)), f));
    }

    [Fact]
    public void Leaves_unrelated_and_unparseable_files_and_subdirectories_alone()
    {
        var untouched = new[]
        {
            "daemon.log", "daemon-notadate.log", "daemon-2026-05-05.log.gz", "provider-probe.log", "headless-errors.log",
            "signalr-C1-T1.log", "crash-oops.log", "notes.txt", "daemon-2026-05-05.txt"
        };
        Touch(untouched);
        var sessionLog = Path.Join(LogsDir, "sessions", "C1-T1", "session.log");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionLog)!);
        File.WriteAllText(sessionLog, "x");
        File.SetLastWriteTimeUtc(sessionLog, Now.UtcDateTime.AddDays(-400));
        var outside = Path.Join(_root, "daemon-2020-01-01.log");
        File.WriteAllText(outside, "x");

        var (deleted, _) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal(0, deleted);
        Assert.All(untouched, f => Assert.True(File.Exists(Path.Join(LogsDir, f)), f));
        Assert.True(File.Exists(sessionLog));
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_negative_keeps_everything(int days)
    {
        Touch("daemon-2020-01-01.log", "crash-20200101-000000.log");

        var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, days);

        Assert.Equal((0, 0), (deleted, failed));
        Assert.Equal(2, Directory.GetFiles(LogsDir).Length);
    }

    [Fact]
    public void Missing_directory_is_a_no_op()
    {
        Assert.Equal((0, 0), DaemonLogRetention.Prune(Path.Join(_root, "nope"), Now, 14));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Undeletable_files_are_counted_not_thrown()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs POSIX permissions and a non-root user");
        Touch("daemon-2026-01-01.log", "daemon-2026-01-02.log", "daemon-2026-01-03.log", "daemon-2026-01-04.log", "daemon-2026-01-05.log");
        File.SetUnixFileMode(LogsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, 14);

            Assert.Equal(0, deleted);
            Assert.Equal(2, failed);
        }
        finally
        {
            File.SetUnixFileMode(LogsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static TheoryData<string> DecoyNames => new()
    {
        "daemon-2020-01-01-notes.log",
        "daemon-2020-01-01 my analysis.log",
        "daemon-2020-01-01.1.log",
        "daemon-2020-01-01.log.bak",
        "daemon-0001-01-01.log",
        "my-daemon-2020-01-01.log",
        "Daemon-2020-01-01.log",
        "crash-20200101-000000-userfile.log",
        "crash-20200101.log",
        "crash-20200101report.log",
        "crash-20200101-000000.log.bak",
        "Crash-20200101-000000.log",
        "crash-00010101-000000.log",
    };

    [Theory]
    [MemberData(nameof(DecoyNames))]
    public void Files_the_daemon_does_not_produce_are_never_deleted(string name)
    {
        // Four genuine newer daemon logs, so the keep-newest-3 rule is not what protects the decoy.
        Touch("daemon-2026-05-17.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log", name);

        var (deleted, failed) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal((0, 0), (deleted, failed));
        Assert.True(File.Exists(Path.Join(LogsDir, name)), name);
    }

    [Fact]
    public void A_matching_name_in_a_subdirectory_is_never_deleted()
    {
        Directory.CreateDirectory(Path.Join(LogsDir, "sessions"));
        Touch("daemon-2026-05-17.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log");
        Touch(Path.Join("sessions", "daemon-2020-01-01.log"), Path.Join("sessions", "crash-20200101-000000.log"));

        var (deleted, _) = DaemonLogRetention.Prune(LogsDir, Now, 14);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Join(LogsDir, "sessions", "daemon-2020-01-01.log")));
        Assert.True(File.Exists(Path.Join(LogsDir, "sessions", "crash-20200101-000000.log")));
    }

    [Fact]
    public void A_clock_far_in_the_future_still_leaves_the_newest_three_daemon_logs()
    {
        var recent = Enumerable.Range(11, 10).Select(d => $"daemon-2026-05-{d}.log").ToArray();
        Touch(recent);

        var (deleted, _) = DaemonLogRetention.Prune(LogsDir, DateTimeOffset.Parse("2099-01-01T00:00:00Z"), 14);

        Assert.Equal(7, deleted);
        Assert.Equal(["daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log"],
            Directory.GetFiles(LogsDir).Select(Path.GetFileName).Order().ToArray());
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(740_000)]
    public void A_retention_longer_than_the_calendar_keeps_everything_without_throwing(int days)
    {
        Touch("daemon-2020-01-01.log", "daemon-2020-01-02.log", "daemon-2020-01-03.log", "daemon-2020-01-04.log");

        Assert.Equal((0, 0), DaemonLogRetention.Prune(LogsDir, Now, days));
        Assert.Equal(4, Directory.GetFiles(LogsDir).Length);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void An_unreadable_logs_directory_is_counted_not_thrown()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs POSIX permissions and a non-root user");
        Touch("daemon-2020-01-01.log");
        File.SetUnixFileMode(LogsDir, UnixFileMode.None);
        try
        {
            Assert.Equal((0, 1), DaemonLogRetention.Prune(LogsDir, Now, 14));
        }
        finally
        {
            File.SetUnixFileMode(LogsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Retention_days_are_read_from_Retention_Logs_Days()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retention:Logs:Days"] = "3"
        }).Build();

        var job = DaemonLogRetention.CreateJob(config, new NetclawPaths(_root), out var warning);

        Assert.Equal(3, job.Days);
        Assert.Null(warning);
    }

    [Fact]
    public void The_old_logging_key_is_not_read()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:File:RetentionDays"] = "3"
        }).Build();

        Assert.Equal(14, DaemonLogRetention.CreateJob(config, new NetclawPaths(_root), out _).Days);
    }

    [Fact]
    public void A_missing_retention_key_uses_the_default()
    {
        var job = DaemonLogRetention.CreateJob(new ConfigurationBuilder().Build(), new NetclawPaths(_root), out var warning);

        Assert.Equal(14, job.Days);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void A_non_integer_retention_falls_back_to_the_default_with_a_warning(string raw)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retention:Logs:Days"] = raw
        }).Build();

        var job = DaemonLogRetention.CreateJob(config, new NetclawPaths(_root), out var warning);

        Assert.Equal(14, job.Days);
        Assert.Contains("Retention:Logs:Days", warning, StringComparison.Ordinal);
        Assert.Contains(raw, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void The_job_prunes_the_logs_directory_of_the_paths_it_was_built_with()
    {
        var paths = new NetclawPaths(_root);
        Touch("daemon-2026-01-01.log", "daemon-2026-05-18.log", "daemon-2026-05-19.log", "daemon-2026-05-20.log", "daemon-2026-05-17.log");
        var job = DaemonLogRetention.CreateJob(new ConfigurationBuilder().Build(), paths, out _);

        var (deleted, failed) = job.Prune(Now);

        Assert.Equal((1, 0), (deleted, failed));
        Assert.False(File.Exists(Path.Join(LogsDir, "daemon-2026-01-01.log")));
    }

    // A wrong clock must not delete the whole crash history either.
    [Fact]
    public void A_clock_far_in_the_future_still_leaves_the_newest_three_crash_logs()
    {
        var crashes = Enumerable.Range(11, 10).Select(d => $"crash-202605{d}-120000.log").ToArray();
        Touch(crashes);

        var (deleted, _) = DaemonLogRetention.Prune(LogsDir, DateTimeOffset.Parse("2099-01-01T00:00:00Z"), 14);

        Assert.Equal(7, deleted);
        Assert.Equal(["crash-20260518-120000.log", "crash-20260519-120000.log", "crash-20260520-120000.log"],
            Directory.GetFiles(LogsDir).Select(Path.GetFileName).Order().ToArray());
    }

    private void Touch(params string[] names)
    {
        foreach (var name in names)
            File.WriteAllText(Path.Join(LogsDir, name), "x");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[DaemonLogRetentionTests] cleanup failed: {ex.Message}");
        }
    }
}
