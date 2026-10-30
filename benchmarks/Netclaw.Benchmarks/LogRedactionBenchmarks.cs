// -----------------------------------------------------------------------
// <copyright file="LogRedactionBenchmarks.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.Logging;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Benchmarks;

/// <summary>
/// Measures the cost that <see cref="RedactingLoggerFactory"/> adds to one enabled record,
/// against the plain <see cref="LoggerFactory"/>. The sink does what the rolling file sink
/// does per record: it formats the message and renders the exception.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class LogRedactionBenchmarks
{
    private ILoggerFactory _directFactory = null!;
    private ILoggerFactory _redactingFactory = null!;
    private ILogger _direct = null!;
    private ILogger _redacting = null!;
    private Exception _exception = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directFactory = new LoggerFactory([new FormattingSink()]);
        _redactingFactory = new RedactingLoggerFactory(new LoggerFactory([new FormattingSink()]));
        _direct = _directFactory.CreateLogger("Netclaw.Benchmarks");
        _redacting = _redactingFactory.CreateLogger("Netclaw.Benchmarks");
        try
        {
            ThrowDeep(6);
        }
        catch (InvalidOperationException ex)
        {
            _exception = ex;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _directFactory.Dispose();
        _redactingFactory.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Information")]
    public void Direct_information()
        => _direct.LogInformation("MCP server '{Name}' connected as generation {Generation} ({Tools} tools)", "github", 3, 49);

    [Benchmark]
    [BenchmarkCategory("Information")]
    public void Redacting_information()
        => _redacting.LogInformation("MCP server '{Name}' connected as generation {Generation} ({Tools} tools)", "github", 3, 49);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Exception")]
    public void Direct_exception()
        => _direct.LogWarning(_exception, "MCP server '{Name}' catalog refresh failed", "github");

    [Benchmark]
    [BenchmarkCategory("Exception")]
    public void Redacting_exception()
        => _redacting.LogWarning(_exception, "MCP server '{Name}' catalog refresh failed", "github");

    private static void ThrowDeep(int depth)
    {
        if (depth == 0)
            throw new InvalidOperationException(
                "The provider returned HTTP 502 Bad Gateway while the daemon refreshed the tool catalog. " +
                new string('x', 400));
        ThrowDeep(depth - 1);
    }

    private sealed class FormattingSink : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger();

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var length = formatter(state, exception).Length;
                if (exception is not null)
                    length += exception.ToString().Length;
                if (length < 0)
                    throw new InvalidOperationException("unreachable");
            }
        }
    }
}
