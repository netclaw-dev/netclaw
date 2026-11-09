// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Akka.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Services;

internal sealed record ConfigReloadBaseline(
    string? NetclawJsonHash,
    IReadOnlyDictionary<string, string?> LowerPriorityConfiguration)
{
    internal static byte[]? ReadNetclawJson(NetclawPaths paths)
    {
        try
        {
            return File.ReadAllBytes(paths.NetclawConfigPath);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Failed to load configuration from file '{paths.NetclawConfigPath}'.", ex);
        }
    }

    internal static string? HashNetclawJson(byte[]? bytes) =>
        bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    internal static ConfigReloadBaseline Load(
        IConfigurationManager configuration, NetclawPaths paths, byte[]? netclawJson)
    {
        var lowerPriority = configuration.AsEnumerable()
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        // ConfigurationManager consumes this stream during Add, before its disposal.
        using var stream = netclawJson is null ? null : new MemoryStream(netclawJson, writable: false);
        configuration.AddNetclawDaemonSources(paths, stream);
        return new ConfigReloadBaseline(HashNetclawJson(netclawJson), lowerPriority);
    }
}

/// <summary>Owns config events, debounce, validation, and restart admission for one host generation.</summary>
internal sealed class ConfigWatcherActor : ReceiveActor, IWithTimers
{
    private const string ReloadTimerKey = "config-reload";
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly NetclawPaths _paths;
    private readonly ConfigReloadBaseline _baseline;
    private readonly IDaemonRestartCoordinator _restartCoordinator;
    private readonly RejectedConfigState _rejectedConfig;
    private readonly IReadOnlyList<ILlmProviderPlugin> _plugins;
    private readonly IModelCapabilityResolver _capabilities;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenRegistration _applicationStartedRegistration;
    private (string Reason, string? Content)? _lastRejection;
    private bool _hostStarted;
    private bool _reloadDue;
    private bool _restartRequested;

    public ConfigWatcherActor(
        NetclawPaths paths,
        ConfigReloadBaseline baseline,
        IDaemonRestartCoordinator restartCoordinator,
        RejectedConfigState rejectedConfig,
        IEnumerable<ILlmProviderPlugin> plugins,
        IModelCapabilityResolver capabilities,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime)
    {
        _paths = paths;
        _baseline = baseline;
        _restartCoordinator = restartCoordinator;
        _rejectedConfig = rejectedConfig;
        _plugins = plugins.ToArray();
        _capabilities = capabilities;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
        _applicationLifetime = applicationLifetime;

        Receive<ConfigChanged>(OnConfigChanged);
        Receive<Status.Failure>(failure => StopHost(failure.Cause));
        ReceiveAsync<HostStarted>(_ =>
        {
            _hostStarted = true;
            return TryReloadAsync();
        });
        ReceiveAsync<ReloadDue>(_ =>
        {
            _reloadDue = true;
            return TryReloadAsync();
        });
    }

    public ITimerScheduler Timers { get; set; } = null!;

    protected override void PreStart()
    {
        base.PreStart();
        var self = Self;
        try
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);
            _watcher = new FileSystemWatcher(_paths.ConfigDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName
            };
            _watcher.Changed += (_, e) => self.Tell(new ConfigChanged(e.ChangeType, e.Name, null));
            _watcher.Created += (_, e) => self.Tell(new ConfigChanged(e.ChangeType, e.Name, null));
            _watcher.Deleted += (_, e) => self.Tell(new ConfigChanged(e.ChangeType, e.Name, null));
            _watcher.Renamed += (_, e) => self.Tell(new ConfigChanged(e.ChangeType, e.Name, e.OldName));
            _watcher.Error += (_, e) => self.Tell(new Status.Failure(e.GetException()));
            _watcher.EnableRaisingEvents = true;
            _log.Info("Config hot-reload watching: {ConfigDir}", _paths.ConfigDirectory);
        }
        catch (Exception ex)
        {
            StopHost(ex);
            return;
        }

        try
        {
            var currentHash = ConfigReloadBaseline.HashNetclawJson(ConfigReloadBaseline.ReadNetclawJson(_paths));
            if (NeedsStartupReload(_baseline.NetclawJsonHash, currentHash))
                ScheduleReload();
        }
        catch (InvalidDataException ex)
        {
            Reject(DaemonConfigurationSources.DescribeLoadFailure(ex), null);
        }

        _applicationStartedRegistration = _applicationLifetime.ApplicationStarted.Register(
            () => self.Tell(HostStarted.Instance));
    }

    internal static bool NeedsStartupReload(string? loadedHash, string? currentHash)
    {
        if (currentHash is null)
            return false;
        // config-startup-reconciliation:begin
        return loadedHash != currentHash;
        // config-startup-reconciliation:end
    }

    private void OnConfigChanged(ConfigChanged change)
    {
        if (_restartRequested)
            return;

        if (change.Name == "netclaw.json" &&
            change.Kind is WatcherChangeTypes.Changed or WatcherChangeTypes.Created or WatcherChangeTypes.Renamed)
            ScheduleReload();
        else if (change.Name == "netclaw.json" && change.Kind == WatcherChangeTypes.Deleted ||
                 change.OldName == "netclaw.json" && change.Kind == WatcherChangeTypes.Renamed)
            _log.Warning("Config file deleted or renamed away. Keeping current config.");
    }

    private void ScheduleReload()
    {
        _reloadDue = false;
        Timers.StartSingleTimer(ReloadTimerKey, ReloadDue.Instance, TimeSpan.FromMilliseconds(500));
    }

    private async Task TryReloadAsync()
    {
        if (!_hostStarted || !_reloadDue || _restartRequested)
            return;
        _reloadDue = false;

        string? contentHash = null;
        string? reason;
        try
        {
            var bytes = ConfigReloadBaseline.ReadNetclawJson(_paths);
            contentHash = ConfigReloadBaseline.HashNetclawJson(bytes);
            using var candidate = new ConfigurationManager();
            candidate.AddInMemoryCollection(_baseline.LowerPriorityConfiguration);
            using var stream = bytes is null ? null : new MemoryStream(bytes, writable: false);
            candidate.AddNetclawDaemonSources(_paths, stream);
            var check = ModelConfigurationValidation.Check(candidate);
            reason = check.Error;
            if (check.Valid is { } valid && valid.Validation.Status == ProviderRuntimeStatus.Valid)
                reason = new ProviderPluginFactory(valid.Providers, _plugins, _capabilities, _loggerFactory, _timeProvider)
                    .Validate(valid.Models);
            // A webhook with no Url stops the reload, as an invalid model does.
            reason ??= NotificationsConfigValidator.Check(candidate);
        }
        catch (InvalidDataException ex)
        {
            reason = DaemonConfigurationSources.DescribeLoadFailure(ex);
        }
        catch (Exception ex)
        {
            _log.Debug("Config check threw: {Error}", ex);
            reason = $"netclaw.json could not be checked: {FirstLine(ex.Message)}";
        }

        if (reason is not null)
        {
            Reject(reason, contentHash);
            return;
        }

        _rejectedConfig.Clear();
        _lastRejection = null;
        _restartRequested = true;
        try
        {
            _log.Info("Config valid. Starting coordinated daemon restart.");
            await _restartCoordinator.RequestConfigRestartAsync(_lifetimeCancellation.Token);
        }
        catch (Exception ex)
        {
            _restartRequested = false;
            _log.Error(ex, "Config reload failed. Keeping previous config.");
        }
    }

    private void Reject(string reason, string? contentHash)
    {
        _rejectedConfig.Reject(reason);
        var rejection = (reason, contentHash);
        if (rejection == _lastRejection)
        {
            _log.Debug("Config is still rejected: {Reason}", reason);
            return;
        }

        _lastRejection = rejection;
        _log.Warning(
            "Config validation failed. {Reason} Keeping the running config: no change from netclaw.json is applied until it is fixed.",
            reason);
    }

    private void StopHost(Exception cause)
    {
        _log.Error(cause, "Config watcher failed. Stopping the daemon.");
        _applicationLifetime.StopApplication();
        Context.Stop(Self);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    protected override void PostStop()
    {
        try
        {
            using (_watcher)
            using (_applicationStartedRegistration)
            using (_lifetimeCancellation)
                _lifetimeCancellation.Cancel();
        }
        finally
        {
            base.PostStop();
        }
    }

    internal sealed record ConfigChanged(WatcherChangeTypes Kind, string? Name, string? OldName)
        : INoSerializationVerificationNeeded;

    internal sealed class HostStarted : INoSerializationVerificationNeeded
    {
        public static HostStarted Instance { get; } = new();
        private HostStarted() { }
    }

    private sealed class ReloadDue : INoSerializationVerificationNeeded
    {
        public static ReloadDue Instance { get; } = new();
        private ReloadDue() { }
    }
}

internal static class ConfigWatcherActorHostingExtensions
{
    public static AkkaConfigurationBuilder WithConfigWatcherActor(this AkkaConfigurationBuilder builder) =>
        builder.StartActors((system, registry, resolver) =>
        {
            var actor = system.ActorOf(resolver.Props<ConfigWatcherActor>(), "config-watcher");
            registry.Register<ConfigWatcherActor>(actor);
        });
}
