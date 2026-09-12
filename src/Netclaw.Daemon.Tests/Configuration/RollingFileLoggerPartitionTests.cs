// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerPartitionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// The provider owns the LOCAL partition of the log stream: a line tagged with a session id by
/// session-SERVING code (an actor's WithContext, which the Akka bridge surfaces alongside a
/// LogSource; or a BeginScope) goes to that session's session.log (Tell'd as a
/// <see cref="SessionLogDiagnostic"/> to the dispatcher) and NOT to daemon.log. A daemon-service
/// line that merely NAMES a session in its message template (a bare {SessionId} field with no
/// LogSource) stays in daemon.log, as does everything sessionless. The session-log writer's own
/// lines are excluded so a write-failure log cannot recurse.
/// </summary>
public sealed class RollingFileLoggerPartitionTests : TestKit
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-05-07T12:00:00Z");

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Session_tagged_line_routes_to_session_log_not_daemon_log()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");

        using (var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow)))
        {
            provider.AttachSessionDispatcher(Task.FromResult(dispatcher.Ref));
            provider.CreateLogger("Akka.Actor.ActorSystem")
                .Log(LogLevel.Information, new EventId(0), ActorState("C1/T1"), null, (_, _) => "spawn requested");

            var diag = await dispatcher.ExpectMsgAsync<SessionLogDiagnostic>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("C1/T1", diag.SessionId.Value);
            Assert.Contains("spawn requested", diag.Line, StringComparison.Ordinal);
        }

        // Partition: the session-tagged line is NOT also in daemon.log.
        Assert.DoesNotContain("spawn requested", ReadDaemonLog(dir), StringComparison.Ordinal);
        Cleanup(dir);
    }

    [Fact]
    public async Task Session_id_carried_in_a_scope_routes()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");
        var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow));

        // A real LoggerFactory wires the external scope provider into the sink, mirroring the
        // chat-client decorators that carry the session id via BeginScope rather than the message.
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        provider.AttachSessionDispatcher(Task.FromResult(dispatcher.Ref));

        var logger = factory.CreateLogger("Netclaw.LlmPipeline");
        using (logger.BeginScope(new[] { new KeyValuePair<string, object>(NetclawLogProperties.SessionId, "C2/T2") }))
            logger.LogInformation("LLM streaming call completed");

        var diag = await dispatcher.ExpectMsgAsync<SessionLogDiagnostic>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("C2/T2", diag.SessionId.Value);
        Assert.Contains("LLM streaming call completed", diag.Line, StringComparison.Ordinal);

        factory.Dispose(); // disposes the provider, draining daemon.log
        Assert.DoesNotContain("LLM streaming call completed", ReadDaemonLog(dir), StringComparison.Ordinal);
        Cleanup(dir);
    }

    [Fact]
    public async Task Sub_agent_line_routes_to_its_own_file_keyed_by_sub_session_id()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");

        using (var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow)))
        {
            provider.AttachSessionDispatcher(Task.FromResult(dispatcher.Ref));

            // A bridged sub-agent line carries the parent SessionId (for OTEL grouping) AND the
            // sub-session id; the LOCAL file is partitioned by the sub-session.
            var state = ActorState("C1/T1", "C1/T1/subagent/summarizer/ab12");
            provider.CreateLogger("Akka.Actor.ActorSystem")
                .Log(LogLevel.Information, new EventId(0), state, null, (_, _) => "sub-agent did work");

            var diag = await dispatcher.ExpectMsgAsync<SessionLogDiagnostic>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("C1/T1", diag.SessionId.Value);
            Assert.Equal("C1/T1/subagent/summarizer/ab12", diag.SubSessionId?.Value);
            Assert.Contains("sub-agent did work", diag.Line, StringComparison.Ordinal);
        }

        Cleanup(dir);
    }

    [Fact]
    public async Task Sub_session_id_carried_in_a_scope_routes_to_the_sub_agent_file()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");
        var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow));
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        provider.AttachSessionDispatcher(Task.FromResult(dispatcher.Ref));

        var logger = factory.CreateLogger("Netclaw.LlmPipeline");
        using (logger.BeginScope(new[]
        {
            new KeyValuePair<string, object>(NetclawLogProperties.SessionId, "C2/T2"),
            new KeyValuePair<string, object>(NetclawLogProperties.SubSessionId, "C2/T2/subagent/coder/cd34"),
        }))
            logger.LogInformation("sub-agent LLM streaming call completed");

        var diag = await dispatcher.ExpectMsgAsync<SessionLogDiagnostic>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("C2/T2", diag.SessionId.Value);
        Assert.Equal("C2/T2/subagent/coder/cd34", diag.SubSessionId?.Value);
        Assert.Contains("sub-agent LLM streaming call completed", diag.Line, StringComparison.Ordinal);

        factory.Dispose();
        Cleanup(dir);
    }

    [Fact]
    public async Task Sessionless_line_goes_to_daemon_log()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");

        using (var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow)))
        {
            provider.AttachSessionDispatcher(Task.FromResult(dispatcher.Ref));
            provider.CreateLogger("Netclaw.Daemon").LogInformation("daemon listening on port 8080");

            await dispatcher.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        }

        Assert.Contains("daemon listening on port 8080", ReadDaemonLog(dir), StringComparison.Ordinal);
        Cleanup(dir);
    }

    [Fact]
    public async Task Daemon_service_line_naming_a_session_in_its_message_stays_in_daemon_log()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");

        using (var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow)))
        {
            provider.AttachSessionDispatcher(Task.FromResult(dispatcher.Ref));

            // A plain daemon-service ILogger<T> line that merely names a session in its message
            // template — no actor LogSource, no scope. It is daemon infrastructure, not the
            // session's own work, so it must NOT be diverted into session.log.
            provider.CreateLogger("Netclaw.Daemon.Gateway.SessionCatalogService")
                .LogWarning("Failed to mark session {SessionId} active", "C7/T7");

            await dispatcher.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        }

        Assert.Contains("Failed to mark session", ReadDaemonLog(dir), StringComparison.Ordinal);
        Cleanup(dir);
    }

    [Fact]
    public async Task Session_log_writers_own_line_is_not_routed_back()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");

        using (var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow)))
        {
            provider.AttachSessionDispatcher(Task.FromResult(dispatcher.Ref));

            // Shape of a bridged Akka log from the SessionLogActor: it carries a SessionId but its
            // ActorPath is under the session-log dispatcher. It must NOT route (feedback guard).
            var state = new List<KeyValuePair<string, object>>
            {
                new(NetclawLogProperties.SessionId, "C3/T3"),
                new("ActorPath", "akka://netclaw/user/session-log-dispatcher/C3%2FT3"),
            };
            provider.CreateLogger("Akka.Actor.ActorSystem")
                .Log(LogLevel.Warning, new EventId(0), state, null, (_, _) => "Failed to flush session.log");

            await dispatcher.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        }

        Assert.Contains("Failed to flush session.log", ReadDaemonLog(dir), StringComparison.Ordinal);
        Cleanup(dir);
    }

    [Fact]
    public async Task Session_line_before_dispatcher_resolves_falls_back_to_daemon_log()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");
        var pending = new TaskCompletionSource<Akka.Actor.IActorRef>();

        using (var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow)))
        {
            provider.AttachSessionDispatcher(pending.Task); // never resolves during this test
            provider.CreateLogger("Akka.Actor.ActorSystem")
                .Log(LogLevel.Information, new EventId(0), ActorState("C4/T4"), null, (_, _) => "before resolve");

            // No buffering: a routable line logged before the dispatcher resolves is not held for
            // it; it goes straight to daemon.log (the dispatcher never sees it).
            await dispatcher.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        }

        Assert.Contains("before resolve", ReadDaemonLog(dir), StringComparison.Ordinal);
        Cleanup(dir);
    }

    [Fact]
    public async Task Dispatcher_resolution_failure_falls_back_and_beacons_to_daemon_log()
    {
        var (dir, daemonPath) = TempPaths();
        var pending = new TaskCompletionSource<Akka.Actor.IActorRef>();
        var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow));
        try
        {
            provider.AttachSessionDispatcher(pending.Task); // dispatcher not resolved
            provider.CreateLogger("Akka.Actor.ActorSystem")
                .Log(LogLevel.Warning, new EventId(0), ActorState("C9/T9"), null, (_, _) => "before failure");

            pending.SetException(new InvalidOperationException("dispatcher never registered"));

            // The session line fell back to daemon.log at log time (dispatcher still null); on
            // failure a single beacon records that per-session routing is disabled.
            await AwaitAssertAsync(
                async () =>
                {
                    var text = await ReadDaemonSharedAsync(dir, TestContext.Current.CancellationToken);
                    Assert.Contains("before failure", text, StringComparison.Ordinal);
                    Assert.Contains("per-session routing disabled", text, StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            provider.Dispose();
        }

        Cleanup(dir);
    }

    private static async Task<string> ReadDaemonSharedAsync(string dir, CancellationToken ct)
    {
        var files = Directory.GetFiles(dir, "daemon-*.log");
        if (files.Length == 0)
            return string.Empty;
        await using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    // Shape of a bridged Akka actor log: WithContext("SessionId") surfaces alongside the actor's
    // LogSource. The LogSource is what marks the line as session-SERVING (vs a daemon service that
    // merely names a session in its message), so the router treats the session id as routable.
    private static List<KeyValuePair<string, object>> ActorState(string sessionId, string? subSessionId = null)
    {
        var state = new List<KeyValuePair<string, object>>
        {
            new(NetclawLogProperties.SessionId, sessionId),
            new("LogSource", "[akka://netclaw/user/session-manager#1]"),
        };
        if (subSessionId is not null)
            state.Add(new(NetclawLogProperties.SubSessionId, subSessionId));
        return state;
    }

    private static (string Dir, string DaemonPath) TempPaths()
    {
        var dir = Path.Join(Path.GetTempPath(), $"netclaw-partition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (dir, Path.Join(dir, "daemon.log"));
    }

    private static string ReadDaemonLog(string dir)
    {
        var files = Directory.GetFiles(dir, "daemon-*.log");
        return files.Length == 0 ? string.Empty : File.ReadAllText(files[0]);
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[RollingFileLoggerPartitionTests] cleanup failed: {ex.Message}");
        }
    }
}
