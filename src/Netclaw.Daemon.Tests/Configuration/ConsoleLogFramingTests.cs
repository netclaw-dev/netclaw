// -----------------------------------------------------------------------
// <copyright file="ConsoleLogFramingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// The daemon's console sink uses the stock single-line formatter. These tests feed it the
/// record that the redaction stage produces and check that each record stays on one line.
/// </summary>
public sealed class ConsoleLogFramingTests
{
    [Theory]
    [InlineData("line-feed")]
    [InlineData("unicode-line-separator")]
    [InlineData("carriage-return")]
    public void A_record_with_a_fake_header_stays_on_one_console_line(string breakKind)
    {
        var separator = breakKind switch
        {
            "line-feed" => "\n",
            "unicode-line-separator" => ((char)0x2028).ToString(),
            "carriage-return" => "\r",
            _ => throw new ArgumentOutOfRangeException(nameof(breakKind)),
        };

        var output = FormatThroughStage("ok" + separator + "17:00:00.000 [INF] fake", exception: null);

        AssertOneLine(output);
    }

    [Fact]
    public void A_record_with_a_stack_trace_stays_on_one_console_line()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("upstream failed");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        var output = FormatThroughStage("refresh failed", exception);

        AssertOneLine(output);
        Assert.Contains("upstream failed", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The formatter folds only <see cref="Environment.NewLine"/> in exception text. An exception
    /// with no secret used to reach it as the original object, so a lone CR or an escape
    /// sequence in its message reached the terminal.
    /// </summary>
    [Theory]
    [InlineData(13)]
    [InlineData(0x2028)]
    [InlineData(0x85)]
    public void An_exception_message_with_a_line_break_stays_on_one_console_line(int lineBreak)
    {
        var exception = new InvalidOperationException(
            $"bad input{(char)0x1B}[2K{(char)lineBreak}17:00:00.000 [INF] fake");

        var output = FormatThroughStage("parse failed", exception);

        AssertOneLine(output);
        Assert.DoesNotContain((char)0x1B, output);
    }

    private static string FormatThroughStage(string message, Exception? exception)
    {
        var capture = new CapturingLoggerProvider();
        new RedactingLoggerFactory(new LoggerFactory([capture]))
            .CreateLogger("Netclaw.Tests")
            .LogWarning(exception, "{Detail}", message);
        var record = Assert.Single(capture.Records);

        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddSimpleConsole(options => options.SingleLine = true))
            .BuildServiceProvider();
        var formatter = services.GetServices<ConsoleFormatter>().Single(f => f.Name == ConsoleFormatterNames.Simple);

        using var writer = new StringWriter();
        formatter.Write(
            new LogEntry<string>(record.Level, record.Category, default, record.Message, record.Exception, static (s, _) => s),
            new LoggerExternalScopeProvider(),
            writer);
        return writer.ToString();
    }

    // One record is one line: apart from its final newline, the output has no character that a
    // terminal or log reader treats as a line break.
    private static void AssertOneLine(string output)
    {
        var body = output.TrimEnd('\r', '\n');
        foreach (var lineBreak in new[] { '\r', '\n', (char)0x85, (char)0x2028, (char)0x2029 })
            Assert.DoesNotContain(lineBreak, body);
    }
}
