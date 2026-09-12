// -----------------------------------------------------------------------
// <copyright file="LlmSessionTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Hosting;
using Netclaw.Configuration;
using Netclaw.Security;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public abstract class LlmSessionTestBase : TestKit
{
    protected LlmSessionTestBase(ITestOutputHelper output) : base(output: output) { }

    /// <summary>
    /// Derived classes that want serialize-messages verification on top of the
    /// shared session pipeline override this to true. Default off because some
    /// derived tests exercise code paths (Akka.Streams stages, vision flows,
    /// sub-agent spawning) where serialize-messages = on causes dispatch issues
    /// unrelated to the marker scheme.
    /// </summary>
    protected virtual bool VerifySerialization => false;

    /// <summary>
    /// Derived watchdog/timer tests override this to run the ActorSystem on
    /// <see cref="Akka.TestKit.TestScheduler"/>, so scheduled work — including the
    /// processing watchdog's <c>ITimerScheduler</c> timers — fires only on an
    /// explicit <see cref="AdvanceScheduler"/>, making timeouts deterministic
    /// instead of racing the wall clock against threadpool scheduling.
    /// </summary>
    protected virtual bool UseTestScheduler => false;

    /// <summary>
    /// Moves virtual scheduler time forward, synchronously delivering any
    /// scheduler items (e.g. the processing watchdog timeout) that fall due.
    /// Only valid when <see cref="UseTestScheduler"/> is true.
    /// </summary>
    protected void AdvanceScheduler(TimeSpan offset) =>
        ((Akka.TestKit.TestScheduler)Sys.Scheduler).Advance(offset);

    /// <summary>
    /// Joins a cold session through its durable subscriber acknowledgement.
    /// The timeout is a fault ceiling for recovery, not an orchestration delay.
    /// </summary>
    protected static async Task<SessionJoined> JoinSessionAsync(
        IActorRef sessionManager,
        Akka.TestKit.TestProbe subscriber,
        SessionId sessionId,
        OutputFilter filter = OutputFilter.TextOnly)
    {
        sessionManager.Tell(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = filter
        });

        return await subscriber.ExpectMsgAsync<SessionJoined>(
            TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    protected sealed override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        if (UseTestScheduler)
            builder.AddHocon(
                "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"",
                HoconAddMode.Prepend);

        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization();

        if (VerifySerialization)
            builder.WithSerializationVerification();

        builder.WithNetclawActors(
            provider.GetRequiredService<ShellExecutionEnvironment>());
    }

    protected sealed override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
        // Mirror production DI (Daemon Program.cs): WithNetclawActors() constructs
        // BackgroundJobManagerActor and ReminderManagerActor via the DI resolver,
        // which need TimeProvider. Without it those actors die with
        // ActorInitializationException at startup — harmless for most session
        // tests but a steady source of restart churn across the shared threadpool
        // that destabilizes real-process integration tests running in parallel.
        services.AddSingleton(TimeProvider.System);
        services.AddTestNetclawPaths();
        services.AddSingleton(SecurityPolicyDefaults.Resolve(null));
        services.AddSingleton<BackgroundJobDefinitionStore>();
        // WithNetclawActors() starts ReminderManagerActor, which resolves these
        // from DI (see Netclaw.Daemon Program.cs). Without them the actor fails
        // activation on every session test. Registered before
        // ConfigureSessionServices so a derived class can still override.
        services.AddSingleton(new SchedulingConfig());
        services.AddSingleton<ReminderDefinitionStore>();
        services.AddSingleton<ReminderHistoryStore>();
        services.AddSingleton<IOperationalNotificationSink>(NullNotificationSink.Instance);
        services.AddSingleton<IReminderChannelNotifier>(NullReminderChannelNotifier.Instance);
        services.AddSingleton<SessionPipeline>();
        services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());
        ConfigureSessionServices(services);
        services.AddLlmSessionCompositeRecords();
    }

    protected virtual void ConfigureSessionServices(IServiceCollection services) { }
}
