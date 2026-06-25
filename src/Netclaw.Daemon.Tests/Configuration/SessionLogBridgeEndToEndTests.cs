// -----------------------------------------------------------------------
// <copyright file="SessionLogBridgeEndToEndTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// End-to-end proof of the essential #1472-step-6 goal: an actor logging through an
/// <c>ILoggingAdapter</c> tagged with <c>WithContext("SessionId", ...)</c> reaches the
/// per-session writer via the REAL Akka→MEL bridge + <see cref="RollingFileLoggerProvider"/>
/// — no AsyncLocal. The bridge packs the event's context properties into the MEL log state;
/// the provider reads the session id off that state and routes a
/// <see cref="SessionLogDiagnostic"/>.
///
/// This uses a hand-built host rather than <c>Akka.Hosting.TestKit</c> on purpose: the
/// TestKit hardwires its Akka→MEL bridge to its own xUnit test-output logger factory, so a
/// TestKit-based test can't observe the bridged log arriving at our provider. The host here
/// replicates production wiring exactly — <c>AddLogging(AddProvider(...))</c> +
/// <c>AddAkka(ConfigureLoggers(AddLoggerFactory))</c>, as in LoggingRegistrationExtensions /
/// Program.cs — so the real bridge routes into our provider.
/// </summary>
public sealed class SessionLogBridgeEndToEndTests : IAsyncLifetime
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-bridge-e2e-{Guid.NewGuid():N}");
    private IHost _host = null!;
    private ActorSystem _system = null!;
    private RollingFileLoggerProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var daemonLogPath = Path.Combine(_basePath, "logs", "daemon.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonLogPath)!);
        _provider = new RollingFileLoggerProvider(daemonLogPath, new FakeTimeProvider(DateTimeOffset.Parse("2026-05-07T12:34:56Z")));

        _host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(builder =>
                {
                    builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                    builder.AddProvider(_provider);
                });
                services.AddAkka("test-system", builder =>
                {
                    builder.ConfigureLoggers(setup =>
                    {
                        setup.ClearLoggers();
                        setup.AddLoggerFactory();
                        setup.LogLevel = Akka.Event.LogLevel.InfoLevel;
                    });
                });
            })
            .Build();

        await _host.StartAsync(TestContext.Current.CancellationToken);
        _system = _host.Services.GetRequiredService<ActorSystem>();
    }

    [Fact]
    public async Task Actor_log_with_session_context_routes_to_session_log_through_the_real_bridge()
    {
        var captured = new TaskCompletionSource<SessionLogDiagnostic>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captor = _system.ActorOf(Props.Create(() => new CaptureActor(captured)), "session-log-captor");
        _provider.AttachSessionDispatcher(Task.FromResult(captor));

        // Exactly how the session/sub-agent actors tag their logger.
        var log = Logging.GetLogger(_system, "Netclaw.Test.SubAgentActor")
            .WithContext("SessionId", "channel/thread");
        log.Info("actor lifecycle line via the real bridge");

        var diagnostic = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("channel/thread", diagnostic.SessionId.Value);
        Assert.Contains("actor lifecycle line via the real bridge", diagnostic.Line, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        finally
        {
            try
            {
                if (Directory.Exists(_basePath))
                    Directory.Delete(_basePath, recursive: true);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[SessionLogBridgeEndToEndTests] cleanup failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"[SessionLogBridgeEndToEndTests] cleanup failed: {ex.Message}");
            }
        }
    }

    private sealed class CaptureActor : ReceiveActor
    {
        public CaptureActor(TaskCompletionSource<SessionLogDiagnostic> captured)
        {
            Receive<SessionLogDiagnostic>(d => captured.TrySetResult(d));
        }
    }
}
