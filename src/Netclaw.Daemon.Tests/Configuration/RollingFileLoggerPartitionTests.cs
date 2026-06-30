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
/// The provider owns the LOCAL partition of the log stream: a line carrying a session id goes
/// to that session's session.log (Tell'd as a <see cref="SessionLogDiagnostic"/> to the
/// dispatcher) and NOT to daemon.log; everything else goes to daemon.log. The session-log
/// writer's own lines are excluded so a write-failure log cannot recurse.
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
            provider.CreateLogger("Netclaw.Tools").LogInformation("spawn requested {SessionId}", "C1/T1");

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
    public async Task Session_lines_emitted_before_dispatcher_resolves_buffer_then_drain()
    {
        var (dir, daemonPath) = TempPaths();
        var dispatcher = CreateTestProbe("dispatcher");
        var pending = new TaskCompletionSource<Akka.Actor.IActorRef>();

        using var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow));
        provider.AttachSessionDispatcher(pending.Task); // routing on, dispatcher not yet resolved
        provider.CreateLogger("Netclaw.Tools").LogInformation("buffered op {SessionId}", "C4/T4");

        await dispatcher.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        pending.SetResult(dispatcher.Ref); // resolution drains the buffer in order
        var diag = await dispatcher.ExpectMsgAsync<SessionLogDiagnostic>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("C4/T4", diag.SessionId.Value);
        Assert.Contains("buffered op", diag.Line, StringComparison.Ordinal);
        Cleanup(dir);
    }

    [Fact]
    public async Task Dispatcher_resolution_failure_drains_buffer_and_beacons_to_daemon_log()
    {
        var (dir, daemonPath) = TempPaths();
        var pending = new TaskCompletionSource<Akka.Actor.IActorRef>();
        var provider = new RollingFileLoggerProvider(daemonPath, new FakeTimeProvider(FixedNow));
        try
        {
            provider.AttachSessionDispatcher(pending.Task); // routing on, dispatcher not resolved
            provider.CreateLogger("Netclaw.Tools").LogInformation("buffered before failure {SessionId}", "C9/T9");

            pending.SetException(new InvalidOperationException("dispatcher never registered"));

            // On failure: the buffered session line is NOT dropped — it falls back to daemon.log,
            // and a single beacon records that routing was disabled.
            await AwaitAssertAsync(
                async () =>
                {
                    var text = await ReadDaemonSharedAsync(dir, TestContext.Current.CancellationToken);
                    Assert.Contains("buffered before failure", text, StringComparison.Ordinal);
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

    private static (string Dir, string DaemonPath) TempPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"netclaw-partition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, "daemon.log"));
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
