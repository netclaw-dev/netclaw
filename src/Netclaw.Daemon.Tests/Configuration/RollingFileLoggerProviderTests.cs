// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RollingFileLoggerProviderTests : TestKit, IAsyncDisposable
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private readonly DisposableTempDir _directory = new();
    private string BasePath => _directory.Path;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

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
    [InlineData("UTC-date-roll", 161, 1, true)]
    public async Task Rotation_preserves_complete_entries_and_bounds_each_segment(
        string scenario, int beforeCount, int afterCount, bool advanceDate)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 23, 59, 59, TimeSpan.Zero));
        var daemonPath = Path.Join(BasePath, "daemon.log");
        var expected = new List<string>();
        var payload = new string('é', 32 * 1024);
        Dictionary<string, byte[]> before;

        using (var provider = new RollingFileLoggerProvider(daemonPath, time))
        {
            await WriteBatch(provider, beforeCount);
            before = Directory.GetFiles(BasePath, "daemon-*.log")
                .ToDictionary(static path => Path.GetFileName(path), static path =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var bytes = new MemoryStream();
                    stream.CopyTo(bytes);
                    return bytes.ToArray();
                });

            if (advanceDate)
            {
                // The complete first batch reaches disk before the same provider crosses UTC midnight.
                Assert.Contains("daemon-2026-05-07.1.log", before.Keys);
                time.Advance(TimeSpan.FromSeconds(1));
                await WriteBatch(provider, afterCount);
            }
        }

        if (!advanceDate && afterCount > 0)
        {
            using var reopened = new RollingFileLoggerProvider(daemonPath, time);
            await WriteBatch(reopened, afterCount);
        }

        AssertCompleteEntries(Directory.GetFiles(BasePath, "daemon-*.log"), expected);

        foreach (var (name, bytes) in before)
        {
            var after = File.ReadAllBytes(Path.Join(BasePath, name!));
            Assert.True(after.Length >= bytes.Length, $"{scenario}: {name} must retain its prior bytes.");
            Assert.True(after.AsSpan(0, bytes.Length).SequenceEqual(bytes), $"{scenario}: {name} must preserve its prefix.");
        }

        var files = Directory.GetFiles(BasePath, "daemon-*.log");
        if (advanceDate)
        {
            Assert.Equal(["daemon-2026-05-07.1.log", "daemon-2026-05-07.log", "daemon-2026-05-08.log"],
                files.Select(Path.GetFileName).Order().ToArray());
            AssertCompleteEntries(files.Where(path => Path.GetFileName(path).StartsWith("daemon-2026-05-07", StringComparison.Ordinal)),
                expected.Take(beforeCount).ToArray());
            AssertCompleteEntries([Path.Join(BasePath, "daemon-2026-05-08.log")], expected.Skip(beforeCount).ToArray());
        }
        else
        {
            // Two size rolls require .1 and .2; each entry stays below the 10 MiB threshold.
            Assert.Equal(["daemon-2026-05-07.1.log", "daemon-2026-05-07.2.log", "daemon-2026-05-07.log"],
                files.Select(Path.GetFileName).Order().ToArray());
        }

        var largestEntryBytes = expected.Max(line => Encoding.UTF8.GetByteCount(line + Environment.NewLine));
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, MaxFileSizeBytes + largestEntryBytes));

        async Task WriteBatch(RollingFileLoggerProvider provider, int count)
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            for (var i = 0; i < count; i++)
            {
                var message = $"entry-{expected.Count.ToString("D4", CultureInfo.InvariantCulture)}:{payload}";
                logger.Log(LogLevel.Information, new EventId(0), message, null, static (state, _) => state);
                expected.Add($"{time.GetUtcNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [INF] Netclaw.Tests: {message}");
            }

            await AwaitAssertAsync(() =>
            {
                AssertCompleteEntries(Directory.GetFiles(BasePath, "daemon-*.log"), expected);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static void AssertCompleteEntries(IEnumerable<string> files, IReadOnlyCollection<string> expected)
    {
        var actual = files.SelectMany(ReadSharedLines).ToArray();
        Assert.Equal(expected.Count, actual.Length);
        Assert.True(expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)),
            "Each complete entry must occur exactly once across all segments.");
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _directory.Dispose();
        }
    }
}
