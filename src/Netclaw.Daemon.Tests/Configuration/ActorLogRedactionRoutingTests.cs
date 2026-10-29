// -----------------------------------------------------------------------
// <copyright file="ActorLogRedactionRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Proves that Akka actor logs go through <see cref="RedactingLoggerFactory"/> and are redacted.
/// </summary>
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
