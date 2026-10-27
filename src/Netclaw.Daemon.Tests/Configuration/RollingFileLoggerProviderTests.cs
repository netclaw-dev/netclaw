// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private readonly DisposableTempDir _directory = new();
    private string BasePath => _directory.Path;

    [Fact]
    public async Task Writes_log_lines_to_the_daily_rolling_daemon_log()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Join(BasePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);

        // Dispose waits for the writer thread before the test reads the file.
        using (var provider = new RollingFileLoggerProvider(daemonLogPath, time))
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            logger.LogInformation("hello daemon log {SessionId}", "channel/thread");
        }

        // No dispatcher was attached, so per-session routing is off and even a session-tagged
        // line falls back to daemon.log. (When routing IS attached the {SessionId} line is
        // partitioned to session.log — see RollingFileLoggerPartitionTests.)
        var daemonLog = Directory.GetFiles(Path.Join(BasePath, "logs"), "daemon-*.log").Single();
        var text = await File.ReadAllTextAsync(daemonLog, TestContext.Current.CancellationToken);
        Assert.Contains("hello daemon log", text, StringComparison.Ordinal);
        Assert.Contains("Netclaw.Tests", text, StringComparison.Ordinal);
        Assert.Contains("channel/thread", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("threshold-crossing", 322, 0, false)]
    [InlineData("same-day-reopen-after-roll", 161, 161, false)]
    [InlineData("UTC-date-roll", 1, 1, true)]
    public void Rotation_preserves_complete_entries_and_bounds_each_segment(
        string scenario, int beforeCount, int afterCount, bool advanceDate)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 34, 56, TimeSpan.Zero));
        var daemonPath = Path.Join(BasePath, "daemon.log");
        var expected = new List<string>();
        var payload = new string('x', 64 * 1024);

        WriteBatch(beforeCount);
        AssertCompleteEntries(BasePath, expected);
        var before = Directory.GetFiles(BasePath, "daemon-*.log")
            .ToDictionary(static path => Path.GetFileName(path), File.ReadAllBytes);

        if (advanceDate)
            time.Advance(TimeSpan.FromDays(1));
        if (afterCount > 0)
        {
            WriteBatch(afterCount);
            AssertCompleteEntries(BasePath, expected);
        }

        foreach (var (name, bytes) in before)
        {
            var after = File.ReadAllBytes(Path.Join(BasePath, name!));
            Assert.True(after.Length >= bytes.Length, $"{scenario}: {name} must retain its prior bytes.");
            Assert.True(after.AsSpan(0, bytes.Length).SequenceEqual(bytes), $"{scenario}: {name} must preserve its prefix.");
        }

        var files = Directory.GetFiles(BasePath, "daemon-*.log");
        if (advanceDate)
        {
            Assert.Equal(["daemon-2026-05-07.log", "daemon-2026-05-08.log"],
                files.Select(Path.GetFileName).Order().ToArray());
        }
        else
        {
            // Two size rolls require .1 and .2; each entry stays below the 10 MiB threshold.
            Assert.Equal(["daemon-2026-05-07.1.log", "daemon-2026-05-07.2.log", "daemon-2026-05-07.log"],
                files.Select(Path.GetFileName).Order().ToArray());
        }

        var largestEntryBytes = expected.Max(line => Encoding.UTF8.GetByteCount(line + Environment.NewLine));
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, MaxFileSizeBytes + largestEntryBytes));

        void WriteBatch(int count)
        {
            using var provider = new RollingFileLoggerProvider(daemonPath, time);
            var logger = provider.CreateLogger("Netclaw.Tests");
            for (var i = 0; i < count; i++)
            {
                var message = $"entry-{expected.Count.ToString("D4", CultureInfo.InvariantCulture)}:{payload}";
                logger.Log(LogLevel.Information, new EventId(0), message, null, static (state, _) => state);
                expected.Add($"{time.GetUtcNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [INF] Netclaw.Tests: {message}");
            }
        }
    }

    private static void AssertCompleteEntries(string directory, List<string> expected)
    {
        var actual = Directory.GetFiles(directory, "daemon-*.log").SelectMany(File.ReadAllLines).ToArray();
        Assert.Equal(expected.Count, actual.Length);
        Assert.True(expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)),
            "Each complete entry must occur exactly once across all segments.");
    }

    public void Dispose() => _directory.Dispose();
}
