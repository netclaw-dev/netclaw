// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeRestartCoordinator _restartCoordinator;
    private readonly FakeTimeProvider _time = new();
    private readonly RejectedConfigState _rejectedConfig = new();
    private readonly CapturingLogger _logger = new();
    private readonly ConfigWatcherService _sut;

    public ConfigWatcherServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _restartCoordinator = new FakeRestartCoordinator();

        var services = new ServiceCollection();
        services.AddSingleton(_paths);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLlmProviders();
        var plugins = services.BuildServiceProvider().GetServices<ILlmProviderPlugin>();

        _sut = new ConfigWatcherService(
            _paths,
            _time,
            _restartCoordinator,
            _rejectedConfig,
            plugins,
            _logger);
    }

    private sealed class CapturingLogger : ILogger<ConfigWatcherService>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings++;
        }
    }

    private const string GoodModels = "\"Models\": { \"Definitions\": { \"d\": { \"Provider\": \"p\", \"ModelId\": \"m\" } }, \"Roles\": { \"Main\": \"d\" } }";

    [Fact]
    public async Task ValidConfigChange_TriggersRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData("""{ "Models": { "Main": { "Provider": "p", "ModelId": "m" }, "Roles": { "Main": "d" } } }""")]
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } } } }""")]
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } }, "Roles": { "Main": "nope" } } }""")]
    // Valid for the resolver, rejected by the rest of the startup check.
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m", "ContextWindow": 100 } }, "Roles": { "Main": "d" } } }""")]
    [InlineData("""{ "Providers": { "p": { "Type": "ollama" } }, "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" }, "f": { "Provider": "zzz", "ModelId": "m" } }, "Roles": { "Main": "d", "Fallback": "f" } } }""")]
    public async Task InvalidModelsSection_DoesNotTriggerRestartAndIsReported(string config)
    {
        File.WriteAllText(_paths.NetclawConfigPath, config);

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.NotNull(_rejectedConfig.Reason);
    }

    [Fact]
    public async Task ValidEditAfterAnInvalidOne_IsAppliedAndClearsTheRejection()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "d" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.NotNull(_rejectedConfig.Reason);

        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": { "p": { "Type": "ollama" } }, "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } }, "Roles": { "Main": "d" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Null(_rejectedConfig.Reason);
        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData("""{ "Models": { "Main": { "Provider": "p", "ModelId": "m" } } }""")]
    [InlineData("""{ "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m" } }, "Roles": { "Main": "d" } } }""")]
    public async Task ValidModelsSection_TriggersRestart(string config)
    {
        File.WriteAllText(_paths.NetclawConfigPath, config);

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
        Assert.Null(_rejectedConfig.Reason);
    }

    [Theory]
    [InlineData("secrets.json")]
    [InlineData("mcp-oauth-metadata.json")]
    [InlineData("random.txt")]
    [InlineData(null)]
    public void NonConfigFiles_AreNotWatched(string? fileName)
    {
        Assert.False(ConfigWatcherService.IsWatchedFile(fileName));
    }

    [Fact]
    public void NetclawJson_IsWatched()
    {
        Assert.True(ConfigWatcherService.IsWatchedFile("netclaw.json"));
    }

    [Fact]
    public async Task InvalidJson_DoesNotTriggerRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ broken json """);
        // secrets.json doesn't exist — that's fine (optional)

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.StartsWith("netclaw.json is not valid JSON", _rejectedConfig.Reason);
        Assert.DoesNotContain('\n', _rejectedConfig.Reason!);
    }

    [Fact]
    public async Task DuplicateModelsKeys_AreRejectedWithTheReason()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "a" } }, "models": { "Roles": { "Main": "b" } } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.StartsWith("Cannot read netclaw.json or secrets.json", _rejectedConfig.Reason);
        Assert.DoesNotContain('\n', _rejectedConfig.Reason!);
    }

    // Each of these passes the Models resolver and fails only where startup builds the provider.
    [Theory]
    [InlineData("""{ "Type": "banana" }""", "Unknown provider type 'banana'. Supported: ollama,")]
    [InlineData("""{ "Type": "openai" }""", "requires authentication")]
    [InlineData("""{ "Type": "anthropic" }""", "requires authentication")]
    [InlineData("""{ "Type": "openai-compatible" }""", "Set Providers:p:Endpoint to a URL.")]
    [InlineData("""{ "Type": "ollama", "AuthMethod": "banana" }""", "Providers:p is invalid: Failed to convert configuration value 'banana' at 'Providers:p:AuthMethod'")]
    [InlineData("""{ "Type": "ollama", "VendorOptions": "x" }""", "Providers:p is invalid: Providers:<name>:VendorOptions must be an object.")]
    [InlineData("""{ "Type": "ollama", "OAuthTokenExpiry": "garbage" }""", "at 'Providers:p:OAuthTokenExpiry'")]
    public async Task ProviderThatStartupCannotBuild_IsRejectedWithTheReason(string provider, string expected)
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"Providers\": { \"p\": " + provider + " }, " + GoodModels + " }");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
        Assert.Contains(expected, _rejectedConfig.Reason);
        Assert.DoesNotContain('\n', _rejectedConfig.Reason!);
        Assert.DoesNotContain("   at ", _rejectedConfig.Reason);
    }

    [Fact]
    public async Task RejectionFollowsTheLatestEdit_NotTheFirstReason()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": { "p": { "Type": "ollama" } }, "Models": { "Definitions": { "d": { "Provider": "p", "ModelId": "m", "ContextWindow": 100 } }, "Roles": { "Main": "d" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.Contains("ContextWindow", _rejectedConfig.Reason);

        // Models is fixed; something else is now wrong.
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"Providers\": { \"p\": { \"Type\": \"ollama\", \"AuthMethod\": \"banana\" } }, " + GoodModels + " }");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.DoesNotContain("ContextWindow", _rejectedConfig.Reason);
        Assert.Contains("Providers:p", _rejectedConfig.Reason);

        File.WriteAllText(_paths.NetclawConfigPath, """{ broken json """);
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.StartsWith("netclaw.json is not valid JSON", _rejectedConfig.Reason);
        Assert.Equal(0, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task UnchangedInvalidFile_WarnsOnce()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "d" } } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);
        await _sut.ApplyReloadAsync(CancellationToken.None);
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.Equal(1, _logger.Warnings);

        File.WriteAllText(_paths.NetclawConfigPath, """{ "Models": { "Roles": { "Main": "e" } } }""");
        await _sut.ApplyReloadAsync(CancellationToken.None);
        Assert.Equal(2, _logger.Warnings);
    }

    [Fact]
    public async Task MissingConfigFiles_TriggersRestart()
    {
        // Both files are optional in the config chain — missing = valid
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task RestartCoordinatorFailure_DoesNotLeaveIngressClosed()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        _restartCoordinator.ThrowOnRequest = true;

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task DaemonSectionChange_TriggersRestart()
    {
        // Regression guard for #1279: Daemon-section changes (bind address, exposure
        // mode) are now applied via the coordinated in-process restart rather than
        // skipped. The restart rebuilds the host and re-binds, and the init wizard
        // relies on this so that writing a new exposure mode actually takes effect.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Port": 9999, "ExposureMode": "reverse-proxy" } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    // File-event tests — drive the watcher's event handlers directly with
    // synthesized event args. Real FileSystemWatcher delivery is OS-dependent
    // and cannot be observed deterministically (especially the latency and
    // event classification of an atomic-replace move on Windows), so the
    // handler -> debounce -> reload pipeline is exercised in isolation and the
    // debounce is virtualized through the injected FakeTimeProvider.

    [Fact]
    public async Task AtomicReplace_TriggersReload()
    {
        // An atomic-replace write (write-temp then rename) surfaces as a Renamed
        // event whose new name is the watched config file.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _sut.OnFileRenamed(this, new RenamedEventArgs(
            WatcherChangeTypes.Renamed, configDir, "netclaw.json", "netclaw.json.tmp.0a1b2c3d"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task InPlaceWrite_TriggersReload()
    {
        // Regression: a direct in-place write (e.g. a shell > redirect) surfaces
        // as a Changed event and must still trigger a reload.
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;

        _sut.OnFileChanged(this, new FileSystemEventArgs(
            WatcherChangeTypes.Changed, configDir, "netclaw.json"));

        _time.Advance(_sut.DebounceInterval);
        await _sut.PendingReload;

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dir.Dispose();
    }

    private sealed class FakeRestartCoordinator : IDaemonRestartCoordinator
    {
        public int RequestCount { get; private set; }

        public bool ThrowOnRequest { get; set; }

        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            RequestCount++;

            if (ThrowOnRequest)
                throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        }
    }
}
