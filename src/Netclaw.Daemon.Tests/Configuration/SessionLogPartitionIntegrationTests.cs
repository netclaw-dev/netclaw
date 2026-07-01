// -----------------------------------------------------------------------
// <copyright file="SessionLogPartitionIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// End-to-end proof through the REAL Akka→MEL bridge: an actor that tags its logger with
/// <c>WithContext("SessionId", …)</c> has its line partitioned into that session's
/// <c>session.log</c> (not <c>daemon.log</c>) by <see cref="RollingFileLoggerProvider"/>. This
/// exercises the live <c>AkkaLogState</c> shape that the unit tests can only simulate, plus the
/// real <c>SessionLogDispatcher</c> and <c>SessionLogActor</c> file write.
/// </summary>
public sealed class SessionLogPartitionIntegrationTests : TestKit
{
    private readonly string _logDir = Path.Join(Path.GetTempPath(), $"netclaw-logpart-int-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-05-07T12:00:00Z"));
    private RollingFileLoggerProvider _provider = null!;

    private string DaemonPath => Path.Join(_logDir, "daemon.log");
    private string SessionsDir => Path.Join(_logDir, "sessions");

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .ConfigureLoggers(setup =>
            {
                setup.ClearLoggers();
                setup.AddLoggerFactory();
                setup.LogLevel = Akka.Event.LogLevel.DebugLevel;
            })
            .WithSessionLogDispatcher(SessionsDir, _time);
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        Directory.CreateDirectory(_logDir);
        _provider = new RollingFileLoggerProvider(DaemonPath, _time);
        // Wire our provider into the host's ILoggerFactory so AddLoggerFactory bridges every
        // Akka actor log to it — the same path production uses.
        services.AddSingleton<ILoggerProvider>(_provider);
    }

    [Fact]
    public async Task Actor_log_tagged_with_session_context_lands_in_session_log_not_daemon_log()
    {
        // Attach the provider to the real dispatcher, as SessionLogDispatcherWiringService does.
        var dispatcher = ActorRegistry.Get<SessionLogDispatcherActorKey>();
        _provider.AttachSessionDispatcher(Task.FromResult(dispatcher));

        var sessionId = new SessionId("intgr-channel/intgr-thread");
        const string marker = "hello from a real actor (partition integration)";

        var probe = Sys.ActorOf(Props.Create(() => new SessionTaggedLogger()), "session-tagged-logger");
        probe.Tell(new LogIt(sessionId.Value, marker));

        var sessionLogPath = SessionLogFile.GetLogPath(sessionId, SessionsDir);
        await AwaitAssertAsync(
            async () =>
            {
                Assert.True(File.Exists(sessionLogPath), "session.log was not created for the tagged actor log");
                var text = await ReadSharedAsync(sessionLogPath, TestContext.Current.CancellationToken);
                Assert.Contains(marker, text, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        // Partition: the session-tagged line is NOT in daemon.log (daemon writes flush per line).
        var daemonFiles = Directory.GetFiles(_logDir, "daemon-*.log");
        var daemonText = daemonFiles.Length == 0
            ? string.Empty
            : await ReadSharedAsync(daemonFiles[0], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(marker, daemonText, StringComparison.Ordinal);

        TryCleanup();
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    private void TryCleanup()
    {
        try
        {
            if (Directory.Exists(_logDir))
                Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[SessionLogPartitionIntegrationTests] cleanup failed: {ex.Message}");
        }
    }

    private sealed record LogIt(string SessionId, string Message);

    private sealed class SessionTaggedLogger : ReceiveActor
    {
        public SessionTaggedLogger()
        {
            Receive<LogIt>(m =>
                Context.GetLogger().WithContext("SessionId", m.SessionId).Info(m.Message));
        }
    }
}
