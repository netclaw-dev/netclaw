// -----------------------------------------------------------------------
// <copyright file="LogRedactionRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Proves that every kind of logger in the daemon host goes through
/// <see cref="RedactingLoggerFactory"/> and is redacted, including providers registered after it.
/// </summary>
public sealed class LogRedactionRoutingTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    [Fact]
    public async Task Daemon_host_loggers_and_framework_loggers_use_the_redacting_factory()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.ConfigureNetclawLogging(new NetclawPaths(_directory.Path));
        // Registered after the stage, as a later sink would be.
        var capture = new CapturingLoggerProvider();
        builder.Logging.AddProvider(capture);
        // This test has no actor system, so the session-log wiring cannot start.
        var wiring = builder.Services.Single(d => d.ImplementationType == typeof(SessionLogDispatcherWiringService));
        builder.Services.Remove(wiring);

        await using var app = builder.Build();
        try
        {
            Assert.IsType<RedactingLoggerFactory>(app.Services.GetRequiredService<ILoggerFactory>());

            await app.StartAsync(TestContext.Current.CancellationToken);
            app.Services.GetRequiredService<ILogger<LogRedactionRoutingTests>>()
                .LogInformation("typed logger marker DB_PASSWORD=hunter2");
            await app.StopAsync(TestContext.Current.CancellationToken);

            Assert.Contains(capture.Records, r => r.Message == "typed logger marker DB_PASSWORD=***REDACTED***");
            Assert.DoesNotContain(capture.Records, r => r.Message.Contains("hunter2", StringComparison.Ordinal));
            Assert.Contains(capture.Records, r => r.Category == "Microsoft.Hosting.Lifetime");
        }
        finally
        {
            app.Services.GetRequiredService<RollingFileLoggerProvider>().Dispose();
        }
    }

    public void Dispose() => _directory.Dispose();
}

public sealed class ActorLogRedactionRoutingTests : TestKit
{
    private readonly CapturingLoggerProvider _capture = new();

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.ConfigureLoggers(setup =>
        {
            setup.ClearLoggers();
            setup.AddLoggerFactory();
            setup.LogLevel = Akka.Event.LogLevel.InfoLevel;
        });

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(_capture);
        services.AddNetclawLogRedaction();
    }

    [Fact]
    public async Task Actor_logs_use_the_redacting_factory()
    {
        var factory = DependencyResolver.For(Sys).Resolver.GetService<ILoggerFactory>();
        Assert.IsType<RedactingLoggerFactory>(factory);

        var actor = Sys.ActorOf(Props.Create(() => new MarkerLogger()));
        actor.Tell("actor logger marker DB_PASSWORD=hunter2");

        await AwaitAssertAsync(
            () => Assert.Contains(_capture.Records, r => r.Message.Contains("actor logger marker DB_PASSWORD=***REDACTED***", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(_capture.Records, r => r.Message.Contains("hunter2", StringComparison.Ordinal));
    }

    private sealed class MarkerLogger : ReceiveActor
    {
        public MarkerLogger() => Receive<string>(message => Context.GetLogger().Info(message));
    }
}
