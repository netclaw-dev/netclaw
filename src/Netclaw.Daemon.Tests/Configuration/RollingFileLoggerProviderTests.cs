// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RollingFileLoggerProviderTests : TestKit, IDisposable
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-rolling-logger-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Mel_structured_log_with_session_id_routes_to_dispatcher()
    {
        // A direct MEL structured log: the {SessionId} placeholder lands in the
        // FormattedLogValues state, which the provider reads to route per-session.
        var (provider, probe) = NewProvider();
        using (provider)
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            logger.LogInformation("session scoped message {SessionId}", "channel/thread");

            var diagnostic = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("channel/thread", diagnostic.SessionId.Value);
            Assert.Contains("session scoped message", diagnostic.Line, StringComparison.Ordinal);
            Assert.Contains("Diagnostic:", diagnostic.Line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Akka_bridged_log_state_with_session_id_routes_to_dispatcher()
    {
        // The Akka→MEL bridge passes the log event's structured properties as the
        // state (an AkkaLogState that enumerates as KeyValuePairs, including the
        // "SessionId" tag from WithContext). The provider reads it off that state.
        var (provider, probe) = NewProvider();
        using (provider)
        {
            var logger = provider.CreateLogger("Netclaw.Actors.SubAgents.SubAgentActor");
            var state = new FakeBridgedLogState(
            [
                new("SessionId", "channel/thread"),
                new("SubSessionId", "channel/thread/subagent/x/1"),
                new("{OriginalFormat}", "actor lifecycle line"),
            ]);
            logger.Log(LogLevel.Information, default, state, exception: null, static (_, _) => "actor lifecycle line");

            var diagnostic = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("channel/thread", diagnostic.SessionId.Value);
            Assert.Contains("actor lifecycle line", diagnostic.Line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Log_without_session_id_does_not_route_to_dispatcher()
    {
        var (provider, probe) = NewProvider();
        using (provider)
        {
            var logger = provider.CreateLogger("Netclaw.Tests");
            logger.LogInformation("daemon message");

            await probe.ExpectNoMsgAsync(
                TimeSpan.FromMilliseconds(200),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var daemonLog = Directory.GetFiles(Path.Combine(_basePath, "logs"), "daemon-*.log", SearchOption.TopDirectoryOnly).Single();
        var daemonText = await File.ReadAllTextAsync(daemonLog, TestContext.Current.CancellationToken);
        Assert.Contains("daemon message", daemonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pre_resolution_diagnostics_buffer_and_drain_to_dispatcher()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);
        var probe = CreateTestProbe();
        var tcs = new TaskCompletionSource<IActorRef>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var provider = new RollingFileLoggerProvider(daemonLogPath, timeProvider))
        {
            provider.AttachSessionDispatcher(tcs.Task);
            var logger = provider.CreateLogger("Netclaw.Tests");

            logger.LogInformation("pre-resolution one {SessionId}", "channel/thread");
            logger.LogInformation("pre-resolution two {SessionId}", "channel/thread");

            await probe.ExpectNoMsgAsync(
                TimeSpan.FromMilliseconds(100),
                cancellationToken: TestContext.Current.CancellationToken);

            tcs.SetResult(probe.Ref);

            var first = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            var second = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("pre-resolution one", first.Line, StringComparison.Ordinal);
            Assert.Contains("pre-resolution two", second.Line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Log_from_async_continuation_still_routes_via_state()
    {
        // The whole point of reading the id off the log state: routing no longer
        // depends on ambient/async context, so a line logged after an await still
        // routes (the old AsyncLocal could be lost across the await).
        var (provider, probe) = NewProvider();
        using (provider)
        {
            var logger = provider.CreateLogger("Netclaw.Tests");

            await Task.Yield();
            logger.LogInformation("post-await {SessionId}", "channel/thread");

            var diagnostic = await probe.ExpectMsgAsync<SessionLogDiagnostic>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("channel/thread", diagnostic.SessionId.Value);
            Assert.Contains("post-await", diagnostic.Line, StringComparison.Ordinal);
        }
    }

    private (RollingFileLoggerProvider Provider, Akka.TestKit.TestProbe Probe) NewProvider()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z"));
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);
        var probe = CreateTestProbe();
        var provider = new RollingFileLoggerProvider(daemonLogPath, timeProvider);
        provider.AttachSessionDispatcher(Task.FromResult<IActorRef>(probe.Ref));
        return (provider, probe);
    }

    // Mimics Akka.Hosting's AkkaLogState: an MEL log state that enumerates as the
    // event's structured properties (the bridge's surface the provider reads).
    private sealed class FakeBridgedLogState(IReadOnlyList<KeyValuePair<string, object?>> fields)
        : IEnumerable<KeyValuePair<string, object?>>
    {
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => fields.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    void IDisposable.Dispose()
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
