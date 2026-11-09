// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Tests.Providers;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Daemon.Services.ConfigWatcherActor;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherActorTests(ITestOutputHelper output) : TestKit(output: output), IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly DisposableTempDir _directory = new();
    private readonly RejectedConfigState _rejected = new();
    private readonly RestartCoordinator _restart = new();
    private readonly ApplicationLifetime _lifetime = new();
    private IEnumerable<ILlmProviderPlugin> _plugins = [];
    private NetclawPaths Paths => new(_directory.Path);
    private const string Models = "\"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\" } }, \"Roles\": { \"Main\": \"d\" } }";

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        var paths = Paths;
        paths.EnsureDirectoriesExist();
        services.AddSingleton(paths);
        services.AddLlmProviders();
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        _plugins = provider.GetServices<ILlmProviderPlugin>().ToArray();
        builder.AddHocon(ConfigurationFactory.ParseString(
            "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\""), HoconAddMode.Prepend);
    }

    private string? WriteCandidate(string? config, string? secrets)
    {
        Paths.EnsureDirectoriesExist();
        if (config is not null) File.WriteAllText(Paths.NetclawConfigPath, config);
        if (secrets is not null) File.WriteAllText(Paths.SecretsPath, secrets);
        return ConfigReloadBaseline.HashNetclawJson(ConfigReloadBaseline.ReadNetclawJson(Paths));
    }

    private IActorRef CreateActor(string? loadedHash) => Sys.ActorOf(Props.Create(() => new ConfigWatcherActor(
        Paths, new ConfigReloadBaseline(loadedHash, new Dictionary<string, string?>()), _restart, _rejected,
        _plugins, new FakeCapabilityResolver(null), NullLoggerFactory.Instance, TimeProvider.System, _lifetime)));

    private Task SettleAsync(IActorRef actor)
        => actor.Ask<ActorIdentity>(new Identify(1), Deadline, TestContext.Current.CancellationToken);

    private async Task AdvanceAsync(IActorRef actor, TimeSpan by)
    {
        await SettleAsync(actor);
        ((TestScheduler)Sys.Scheduler).Advance(by);
        await SettleAsync(actor);
    }

    public static TheoryData<string, string?, string?, bool, int> StartupCases => new()
    {
        { "same_bytes", "{}", "{}", false, 0 },
        { "different_bytes", "{}", "older", false, 1 },
        { "file_appeared", "{}", null, false, 1 },
        { "file_disappeared", null, "{}", false, 0 },
        { "read_error", "{}", "{}", true, 0 }
    };

    [Theory]
    [MemberData(nameof(StartupCases))]
    public async Task Startup_reconciliation_uses_the_loaded_hash(
        string name, string? current, string? loaded, bool unreadable, int expectedRestarts)
    {
        WriteCandidate(current, null);
        if (unreadable)
        {
            File.Delete(Paths.NetclawConfigPath);
            Directory.CreateDirectory(Paths.NetclawConfigPath);
        }
        var actor = CreateActor(ConfigReloadBaseline.HashNetclawJson(loaded is null ? null : Encoding.UTF8.GetBytes(loaded)));
        actor.Tell(HostStarted.Instance);
        await AdvanceAsync(actor, Debounce);

        Assert.True(expectedRestarts == _restart.Count, name);
        if (unreadable) Assert.Contains("netclaw.json", _rejected.Reason);
        else Assert.Null(_rejected.Reason);
    }

    private const string PriorRejection = "an earlier rejected edit";
    public static TheoryData<string, string, string?, WatcherChangeTypes, string?, string?, string?> ReloadCases => new()
    {
        { "changed_with_secrets_overlay", "{ \"Providers\": { \"p\": { \"Type\": \"banana\" } }, " + Models + " }",
            "{ \"Providers\": { \"p\": { \"Type\": \"ollama\" } } }", WatcherChangeTypes.Changed, "netclaw.json", null, null },
        { "created", "{}", null, WatcherChangeTypes.Created, "netclaw.json", null, null },
        { "renamed_into", "{}", null, WatcherChangeTypes.Renamed, "netclaw.json", "config.tmp", null },
        { "secrets_only", "{}", null, WatcherChangeTypes.Changed, "secrets.json", null, PriorRejection },
        { "deleted", "{}", null, WatcherChangeTypes.Deleted, "netclaw.json", null, PriorRejection },
        { "renamed_away", "{}", null, WatcherChangeTypes.Renamed, "config.old", "netclaw.json", PriorRejection },
        { "other_file", "{}", null, WatcherChangeTypes.Changed, "other.txt", null, PriorRejection },
        { "invalid_model", "{ \"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\", \"ContextWindow\": 100 } }, \"Roles\": { \"Main\": \"d\" } } }",
            null, WatcherChangeTypes.Changed, "netclaw.json", null, "ContextWindow" },
        { "invalid_provider", "{ \"Providers\": { \"p\": { \"Type\": \"banana\" } }, " + Models + " }",
            null, WatcherChangeTypes.Changed, "netclaw.json", null, "Unknown provider type 'banana'" },
        // A keyed webhook with no Url from any source stops the reload, as an invalid model does.
        { "webhook_without_url", "{ \"Notifications\": { \"Webhooks\": { \"ops\": { \"Format\": \"Slack\" } } } }",
            null, WatcherChangeTypes.Changed, "netclaw.json", null, "Notifications:Webhooks:ops:Url" },
        { "webhook_url_from_secrets", "{ \"Notifications\": { \"Webhooks\": { \"ops\": { \"Format\": \"Slack\" } } } }",
            "{ \"Notifications\": { \"Webhooks\": { \"ops\": { \"Url\": \"https://hooks.example.test/x\" } } } }",
            WatcherChangeTypes.Changed, "netclaw.json", null, null }
    };

    [Theory]
    [MemberData(nameof(ReloadCases))]
    public async Task File_events_validate_the_current_candidate_and_preserve_rejection_state(
        string name, string config, string? secrets, WatcherChangeTypes kind, string? file, string? oldFile, string? expectedReason)
    {
        var hash = WriteCandidate(config, secrets);
        _rejected.Reject(PriorRejection);
        var actor = CreateActor(hash);
        actor.Tell(HostStarted.Instance);
        actor.Tell(new ConfigChanged(kind, file, oldFile));
        await AdvanceAsync(actor, Debounce);

        Assert.True(_restart.Count == (expectedReason is null ? 1 : 0), name);
        if (expectedReason is null) Assert.Null(_rejected.Reason);
        else
        {
            Assert.Contains(expectedReason, _rejected.Reason);
            Assert.DoesNotContain('\n', _rejected.Reason!);
            Assert.DoesNotContain("   at ", _rejected.Reason);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_due_reload_waits_for_host_start_or_actor_stop(bool stop)
    {
        WriteCandidate("{ broken json", null);
        var actor = CreateActor("older-hash");
        await AdvanceAsync(actor, Debounce);
        Assert.Null(_rejected.Reason);
        Assert.Equal(0, _restart.Count);

        if (stop)
        {
            await WatchAsync(actor);
            Sys.Stop(actor);
            await ExpectTerminatedAsync(actor, Deadline, cancellationToken: TestContext.Current.CancellationToken);
            _lifetime.NotifyStarted();
            ((TestScheduler)Sys.Scheduler).Advance(Debounce);
            Assert.Null(_rejected.Reason);
        }
        else
        {
            _lifetime.NotifyStarted();
            await SettleAsync(actor);
            Assert.StartsWith("Couldn't read netclaw.json:", _rejected.Reason);
        }
        Assert.Equal(0, _restart.Count);
    }

    [Fact]
    public async Task A_new_event_replaces_the_debounce_and_ignored_events_preserve_it()
    {
        var actor = CreateActor(WriteCandidate("{}", null));
        actor.Tell(HostStarted.Instance);
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
        await AdvanceAsync(actor, Debounce / 2);
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Created, "netclaw.json", null));
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Deleted, "netclaw.json", null));
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Renamed, "config.old", "netclaw.json"));
        await AdvanceAsync(actor, Debounce / 2);
        Assert.Equal(0, _restart.Count);
        await AdvanceAsync(actor, Debounce / 2);
        Assert.Equal(1, _restart.Count);
    }

    [Fact]
    public async Task Repeated_invalid_events_warn_once()
    {
        var actor = CreateActor(WriteCandidate("{ broken json", null));
        actor.Tell(HostStarted.Instance);
        await EventFilter.Warning(contains: "Config validation failed.").ExpectAsync(1, async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
                await AdvanceAsync(actor, Debounce);
            }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, _restart.Count);
    }

    public enum RestartEnd { Complete, Stop, FailThenRetry }

    [Theory]
    [InlineData(RestartEnd.Complete)]
    [InlineData(RestartEnd.Stop)]
    [InlineData(RestartEnd.FailThenRetry)]
    public async Task Coordinator_outcomes_preserve_one_admission_and_allow_only_an_event_retry(RestartEnd outcome)
    {
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _restart.Response = response.Task;
        var actor = CreateActor(WriteCandidate("{}", null));
        await WatchAsync(actor);
        actor.Tell(HostStarted.Instance);
        actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
        try
        {
            await SettleAsync(actor);
            ((TestScheduler)Sys.Scheduler).Advance(Debounce);
            await _restart.Entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            if (outcome != RestartEnd.FailThenRetry)
                actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
            if (outcome == RestartEnd.Stop)
            {
                Sys.Stop(actor);
                await ExpectTerminatedAsync(actor, Deadline, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(_restart.Token.IsCancellationRequested);
            }
            else
            {
                _restart.Response = Task.CompletedTask;
                if (outcome == RestartEnd.FailThenRetry) response.TrySetException(new InvalidOperationException("The test restart failed."));
                else response.TrySetResult();
                await SettleAsync(actor);
                Assert.Equal(1, _restart.Count);
                if (outcome == RestartEnd.FailThenRetry)
                    actor.Tell(new ConfigChanged(WatcherChangeTypes.Changed, "netclaw.json", null));
                await AdvanceAsync(actor, Debounce);
            }
            Assert.Equal(outcome == RestartEnd.FailThenRetry ? 2 : 1, _restart.Count);
        }
        finally { response.TrySetResult(); }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { _lifetime.Dispose(); _directory.Dispose(); }
    }

    private sealed class ApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void NotifyStarted() => _started.Cancel();
        public void StopApplication() => throw new InvalidOperationException("The test actor could not start its watcher.");
        public void Dispose() => _started.Dispose();
    }

    private sealed class RestartCoordinator : IDaemonRestartCoordinator
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task Response { get; set; } = Task.CompletedTask;
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            var response = Response;
            Token = cancellationToken;
            Interlocked.Increment(ref _count);
            Entered.TrySetResult();
            return response.WaitAsync(cancellationToken);
        }
    }
}
