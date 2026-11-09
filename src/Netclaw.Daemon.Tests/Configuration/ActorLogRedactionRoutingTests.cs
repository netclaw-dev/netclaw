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

    [Fact]
    public async Task An_actor_value_that_only_the_template_marks_as_a_secret_is_masked()
    {
        var actor = Sys.ActorOf(Props.Create(() => new TemplateLogger()));
        actor.Tell("abc123xyz");

        await AwaitAssertAsync(
            () => Assert.Contains(_capture.Records, r => r.Message.Contains("actor template marker", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var record = Assert.Single(_capture.Records, r => r.Message.Contains("actor template marker", StringComparison.Ordinal));
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("***REDACTED***", values["Token"]);
        Assert.Equal("call-7", values["CallId"]);
        Assert.Contains("ActorPath", values.Keys);
        Assert.DoesNotContain("abc123xyz", record.Message, StringComparison.Ordinal);
    }

    private sealed class MarkerLogger : ReceiveActor
    {
        public MarkerLogger() => Receive<string>(message => Context.GetLogger().Info(message));
    }

    private sealed class TemplateLogger : ReceiveActor
    {
        public TemplateLogger() => Receive<string>(token =>
            Context.GetLogger().Info("actor template marker call={CallId} Authorization: Bearer {Token}", "call-7", token));
    }
}
