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
using Netclaw.Tests.Utilities;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public abstract class LlmSessionTestBase : TestKit, IAsyncDisposable
{
    private TestSessionTempDirectory? _testTempDir;

    protected LlmSessionTestBase(ITestOutputHelper output) : base(output: output) { }

    protected NetclawPaths TestPaths => _testTempDir?.Paths
        ?? throw new InvalidOperationException("Test paths are not initialized.");

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

    /// <summary>Configures the journal and snapshot stores for a session test host.</summary>
    protected virtual void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
        => builder.WithInMemoryJournal().WithInMemorySnapshotStore();

    /// <summary>Configures the Netclaw serializer for a session test host.</summary>
    protected virtual void ConfigureSessionSerialization(AkkaConfigurationBuilder builder)
        => builder.WithNetclawSerialization();

    /// <summary>
    /// Moves virtual scheduler time forward, synchronously delivering any
    /// scheduler items (e.g. the processing watchdog timeout) that fall due.
    /// Only valid when <see cref="UseTestScheduler"/> is true.
    /// </summary>
    protected void AdvanceScheduler(TimeSpan offset) =>
        ((Akka.TestKit.TestScheduler)Sys.Scheduler).Advance(offset);

    /// <summary>
    /// The fault ceiling for a cold persistent-actor recovery. It is not an
    /// orchestration delay. A cold start creates the persistence plugins, can
    /// create their tables, and replays the journal. On a loaded CI runner
    /// that work can take more than 10 seconds.
    /// </summary>
    protected static readonly TimeSpan ColdRecoveryCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Joins a cold session through its durable subscriber acknowledgement.
    /// The timeout is <see cref="ColdRecoveryCeiling"/>.
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
            ColdRecoveryCeiling,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    protected sealed override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        if (UseTestScheduler)
            builder.AddHocon(
                "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"",
                HoconAddMode.Prepend);

        ConfigureSessionPersistence(builder);
        ConfigureSessionSerialization(builder);

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
        // Own a unique temp directory for this test and register it (plus its
        // NetclawPaths) so SessionServices can construct. Disposed in
        // DisposeAsync so the /tmp tree is not leaked (issue #2266).
        _testTempDir = TestSessionTempDirectory.Create("netclaw-llm-session-");
        services.AddSingleton(_testTempDir);
        services.AddSingleton(_testTempDir.Paths);
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
        // The daemon always registers a trust deriver, and a tool batch requires the effective trust context.
        // A Personal posture narrows nothing, so each turn keeps the audience of its source. A derived class can
        // register another deriver, or remove this one to test a session without a trust context.
        services.AddSingleton(new TrustContextDeriver(new EffectivePolicyDefaults(DeploymentPosture.Personal,
            TrustAudience.Personal, ShellExecutionMode.HostAllowed, UsedStrictFallback: false)));
        ConfigureSessionServices(services);
        services.AddLlmSessionCompositeRecords();
    }

    protected virtual void ConfigureSessionServices(IServiceCollection services) { }

    /// <summary>
    /// Runs <paramref name="join"/>, which recovers the session, and returns the Info lines of the session that contain
    /// <paramref name="text"/> and come before its join line. The session logs its recovery lines before it handles the
    /// stashed join, and the event stream keeps that order. So the join line ends the search, and a spec can show that a
    /// recovery line is absent without a wait for a deadline.
    /// </summary>
    protected async Task<IReadOnlyList<string>> RecoveryLogLinesAsync(SessionId sessionId, string text, Func<Task> join)
    {
        var logs = CreateTestProbe();
        Sys.EventStream.Subscribe(logs.Ref, typeof(Akka.Event.Info));
        try
        {
            await join();
            var source = $"/session-manager/{Uri.EscapeDataString(sessionId.Value)}";
            var lines = new List<string>();
            while (true)
            {
                var info = await logs.ExpectMsgAsync<Akka.Event.Info>(ColdRecoveryCeiling,
                    cancellationToken: TestContext.Current.CancellationToken);
                var line = info.Message?.ToString() ?? string.Empty;
                if (!info.LogSource.EndsWith(source, StringComparison.Ordinal))
                    continue;
                if (line.Contains(" joined (filter=", StringComparison.Ordinal))
                    return lines;
                if (line.Contains(text, StringComparison.Ordinal))
                    lines.Add(line);
            }
        }
        finally
        {
            Sys.EventStream.Unsubscribe(logs.Ref);
        }
    }

    /// <summary>
    /// Deletes the directories that a derived class owns outside <see cref="TestPaths"/>.
    /// Runs after TestKit has disposed. A derived class cannot re-implement
    /// <see cref="IAsyncDisposable.DisposeAsync"/> without skipping the cleanup below.
    /// </summary>
    protected virtual void DeleteOwnedDirectories() { }

    // TestKit stops the actor system only after AfterAllAsync returns, so an actor can
    // still write into the temp directory until then (issue #2266). Delete the
    // directory after TestKit has disposed. The finally block runs the cleanup even
    // when the base teardown throws.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            try
            {
                DeleteOwnedDirectories();
            }
            finally
            {
                if (_testTempDir is not null)
                    await _testTempDir.DisposeAsync();
            }
        }
    }
}
