// -----------------------------------------------------------------------
// <copyright file="LogRecordFramingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Log content must not be able to forge a record in daemon.log. These tests write through the
/// redacting factory into the real file sink and read the file back.
/// </summary>
public sealed class LogRecordFramingTests : IDisposable
{
    private static readonly Regex Header = new(@"^\d{2}:\d{2}:\d{2}\.\d{3} \[", RegexOptions.CultureInvariant);
    private readonly DisposableTempDir _directory = new();

    [Fact]
    public void A_fake_header_after_a_line_feed_stays_inside_the_record()
    {
        var lines = WriteAndRead(logger => logger.LogInformation("ok\n17:00:00.000 [INF] fake"));

        Assert.Single(lines, line => Header.IsMatch(line));
        Assert.Contains("  17:00:00.000 [INF] fake", lines);
    }

    [Fact]
    public void A_fake_header_after_a_unicode_line_separator_stays_inside_the_record()
    {
        var lines = WriteAndRead(logger => logger.LogInformation("ok" + (char)0x2028 + "17:00:00.000 [INF] fake"));

        Assert.Single(lines, line => Header.IsMatch(line));
        Assert.Contains("  17:00:00.000 [INF] fake", lines);
    }

    [Fact]
    public void A_terminal_escape_sequence_is_written_as_text()
    {
        var lines = WriteAndRead(logger => logger.LogInformation("red" + (char)0x1B + "[31m"));

        var record = Assert.Single(lines);
        Assert.EndsWith("red" + "\\" + "u001B[31m", record, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x1B, record);
    }

    [Fact]
    public void A_stack_trace_keeps_one_frame_per_prefixed_line()
    {
        var exception = Capture(() => ThrowAfter(3));

        var lines = WriteAndRead(logger => logger.LogError(exception, "refresh failed"));

        Assert.Single(lines, line => Header.IsMatch(line));
        var frames = lines.Where(line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal)).ToList();
        Assert.True(frames.Count >= 4, $"expected at least 4 frames, found {frames.Count}");
        Assert.All(lines.Skip(1), line => Assert.StartsWith("  ", line, StringComparison.Ordinal));
    }

    private IReadOnlyList<string> WriteAndRead(Action<ILogger> write)
    {
        var path = Path.Join(_directory.Path, "daemon.log");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-29T17:00:00Z"));
        // Dispose waits for the writer thread before the test reads the file.
        using (var provider = new RollingFileLoggerProvider(path, time))
        using (var factory = new RedactingLoggerFactory(new LoggerFactory([provider])))
        {
            write(factory.CreateLogger("Netclaw.Tests"));
        }

        var file = Directory.GetFiles(_directory.Path, "daemon-*.log").Single();
        return File.ReadAllText(file).Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("expected an exception");
    }

    private static void ThrowAfter(int depth)
    {
        if (depth == 0)
            throw new InvalidOperationException("upstream failed");
        ThrowAfter(depth - 1);
    }

    public void Dispose() => _directory.Dispose();
}
