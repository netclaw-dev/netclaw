// -----------------------------------------------------------------------
// <copyright file="TelegramChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramChannel : IChannel
{
    internal static readonly TimeSpan RetryCheckInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly ISessionPipeline _pipeline;
    private readonly SessionIngressGate _ingressGate;
    private readonly ActorSystem _actorSystem;
    private readonly IActorRegistry _actorRegistry;
    private readonly TelegramChannelOptions _options;
    private readonly TelegramTransport _transport;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly IContentScanner _contentScanner;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly ISessionStorageResolver _storageResolver;
    private readonly IChannelRegistry _channelRegistry;
    private readonly TimeProvider _timeProvider;

    private IActorRef? _gateway;
    private volatile bool _connected;
    private volatile string? _failureDetail;

    // This token stops the startup retry supervisor before transport disposal.
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _startupRetryTask;
    private int _retryFailureCount;
    private DateTimeOffset _nextRetryAttemptAt = DateTimeOffset.MinValue;

    public TelegramChannel(
        ISessionPipeline pipeline,
        SessionIngressGate ingressGate,
        ActorSystem actorSystem,
        IActorRegistry actorRegistry,
        TelegramChannelOptions options,
        TelegramTransport transport,
        ILogger<TelegramChannel> logger,
        IContentScanner contentScanner,
        ToolConfig toolConfig,
        ModelCapabilities modelCapabilities,
        ISessionStorageResolver storageResolver,
        IChannelRegistry channelRegistry,
        TimeProvider timeProvider)
    {
        _pipeline = pipeline;
        _ingressGate = ingressGate;
        _actorSystem = actorSystem;
        _actorRegistry = actorRegistry;
        _options = options;
        _transport = transport;
        _logger = logger;
        _contentScanner = contentScanner;
        _audienceProfiles = toolConfig.AudienceProfiles;
        _modelCapabilities = modelCapabilities;
        _storageResolver = storageResolver;
        _channelRegistry = channelRegistry;
        _timeProvider = timeProvider;
    }

    public ChannelType ChannelType => ChannelType.Telegram;

    public string DisplayName => "Telegram";

    internal IActorRef? Gateway => _gateway;

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Degraded, "Telegram channel disabled."));

        return ValueTask.FromResult(_connected
            ? new ChannelHealth(ChannelHealthStatus.Healthy)
            : new ChannelHealth(ChannelHealthStatus.Disconnected, _failureDetail ?? "Telegram bot disconnected."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram channel disabled by configuration.");
            return;
        }

        try
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Telegram channel connected. The bot is ready for messages.");
        }
        catch (Exception ex)
        {
            // A channel that cannot connect must never escape StartAsync: an
            // unhandled exception aborts the .NET host and crashes the daemon.
            // A misconfigured or unreachable channel degrades instead.
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Telegram channel start cancelled during shutdown.");
                return;
            }

            HandleConnectFailure(TelegramConnectFailureClassifier.Classify(ex));
        }
    }

    /// <summary>
    /// One connection attempt. Gateway creation, registry binding, and event
    /// subscription are idempotent so startup retries reuse them unchanged.
    /// </summary>
    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        EnsureGateway();
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _connected = true;
        _failureDetail = null;
    }

    private void EnsureGateway()
    {
        if (_gateway is not null)
            return;

        _gateway = _actorSystem.ActorOf(
            TelegramGatewayActor.CreateProps(new TelegramGatewayDependencies(
                _pipeline,
                _ingressGate,
                _options,
                _transport,
                _contentScanner,
                _audienceProfiles,
                _modelCapabilities,
                _storageResolver,
                _channelRegistry)),
            "telegram-gateway");

        _actorRegistry.Register<TelegramGatewayActorKey>(_gateway);
        _transport.MessageReceived += HandleMessageAsync;
        _transport.CallbackReceived += HandleCallbackAsync;
        _transport.PollingFailed += HandlePollingFailureAsync;
        _transport.PollingRecovered += HandlePollingRecoveryAsync;
    }

    private void HandleConnectFailure(ChannelConnectException failure)
    {
        _failureDetail = failure.Message;

        if (failure.IsFatal)
        {
            // Retrying cannot help — the operator must fix the configuration.
            // The rest of the daemon keeps running.
            _logger.LogError(
                "Telegram channel could not connect and will stay offline until the "
                + "daemon restarts. The rest of the daemon is unaffected. {Reason}",
                failure.Message);
            return;
        }

        _logger.LogWarning(
            "Telegram channel could not connect (transient). The daemon keeps running "
            + "and retries the start in the background. {Reason}",
            failure.Message);
        StartStartupRetryLoop();
    }

    /// <summary>
    /// Retries the initial start until it succeeds. Telegram.Bot owns polling
    /// recovery after a successful start, so this supervisor ends on the first
    /// success instead of watching the connection.
    /// </summary>
    private void StartStartupRetryLoop()
    {
        if (_startupRetryTask is { IsCompleted: false })
            return;

        _startupRetryTask = RunStartupRetryAsync(_lifetimeCts.Token);
    }

    private async Task RunStartupRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(RetryCheckInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = _timeProvider.GetUtcNow();
                if (now < _nextRetryAttemptAt)
                    continue;

                try
                {
                    await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Telegram channel connected after {FailedAttempts} failed start attempt(s).",
                        _retryFailureCount);
                    ResetRetryBackoff();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var classified = TelegramConnectFailureClassifier.Classify(ex);
                    _failureDetail = classified.Message;

                    if (classified.IsFatal)
                    {
                        _logger.LogError(
                            "Telegram retry found a fatal failure. The channel will stay "
                            + "offline until the daemon restarts. {Reason}",
                            classified.Message);
                        return;
                    }

                    _retryFailureCount++;
                    var retryDelay = ComputeRetryDelay(_retryFailureCount);
                    _nextRetryAttemptAt = now + retryDelay;
                    _logger.LogWarning(
                        "Telegram start retry attempt {Attempt} failed. The next attempt "
                        + "starts in {RetryDelay}. {Reason}",
                        _retryFailureCount,
                        retryDelay,
                        classified.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Telegram startup retry loop stopped with the channel.");
        }
    }

    internal static TimeSpan ComputeRetryDelay(int failureCount)
    {
        if (failureCount <= 0)
            return TimeSpan.Zero;

        var exponent = Math.Min(failureCount - 1, 16);
        var ticks = RetryCheckInterval.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(ticks, MaxRetryDelay.Ticks));
    }

    private void ResetRetryBackoff()
    {
        _retryFailureCount = 0;
        _nextRetryAttemptAt = DateTimeOffset.MinValue;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the startup retry supervisor before transport disposal.
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        if (_startupRetryTask is { } retryTask)
        {
            try
            {
                await retryTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telegram startup retry loop ended with an error during shutdown.");
            }
        }

        _connected = false;
        _transport.MessageReceived -= HandleMessageAsync;
        _transport.CallbackReceived -= HandleCallbackAsync;
        _transport.PollingFailed -= HandlePollingFailureAsync;
        _transport.PollingRecovered -= HandlePollingRecoveryAsync;
        await _transport.StopAsync().ConfigureAwait(false);

        if (_gateway is not null)
        {
            try
            {
                await _gateway.GracefulStop(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                _actorSystem.Stop(_gateway);
            }

            _gateway = null;
        }

        _lifetimeCts.Dispose();
    }

    private Task HandleMessageAsync(TelegramInboundMessage message)
    {
        _gateway?.Tell(message);
        return Task.CompletedTask;
    }

    private Task HandleCallbackAsync(TelegramCallbackQuery callback)
    {
        _gateway?.Tell(callback);
        return Task.CompletedTask;
    }

    private Task HandlePollingFailureAsync(string detail)
    {
        _connected = false;
        _failureDetail = detail;
        return Task.CompletedTask;
    }

    private Task HandlePollingRecoveryAsync()
    {
        _connected = true;
        _failureDetail = null;
        return Task.CompletedTask;
    }
}
