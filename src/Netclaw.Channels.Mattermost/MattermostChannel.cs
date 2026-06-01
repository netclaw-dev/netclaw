// -----------------------------------------------------------------------
// <copyright file="MattermostChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Mattermost;

public sealed class MattermostChannel : IChannel
{
    private readonly ActorSystem _system;
    private readonly ISessionPipeline _pipeline;
    private readonly SessionIngressGate _ingressGate;
    private readonly IMattermostGatewayClient _gatewayClient;
    private readonly IMattermostReplyClient _replyClient;
    private readonly IContentScanner _contentScanner;
    private readonly IPromptInjectionDetector _promptInjectionDetector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThreadHistoryFetcher? _threadHistoryFetcher;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly MattermostChannelOptions _options;
    private readonly ILogger<MattermostChannel> _logger;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly NetclawPaths _paths;
    private readonly MattermostCallbackActionStore? _callbackActionStore;

    // Cancels the background reconnect loop when the channel stops.
    private readonly CancellationTokenSource _lifetimeCts = new();

    private IActorRef? _gateway;
    private Task? _reconnectTask;
    private string? _connectFailureDetail;

    internal IActorRef? Gateway => _gateway;
    internal IMattermostGatewayClient GatewayClient => _gatewayClient;

    internal MattermostChannelId? DefaultChannelId =>
        !string.IsNullOrWhiteSpace(_options.DefaultChannelId)
            ? new MattermostChannelId(_options.DefaultChannelId)
            : null;

    public MattermostChannel(
        ActorSystem system,
        ISessionPipeline pipeline,
        SessionIngressGate ingressGate,
        IMattermostGatewayClient gatewayClient,
        IMattermostReplyClient replyClient,
        IContentScanner contentScanner,
        IPromptInjectionDetector? promptInjectionDetector,
        IHttpClientFactory httpClientFactory,
        IThreadHistoryFetcher? threadHistoryFetcher,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        MattermostChannelOptions options,
        ILogger<MattermostChannel> logger,
        ToolConfig toolConfig,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths,
        MattermostCallbackActionStore? callbackActionStore = null)
    {
        _system = system;
        _pipeline = pipeline;
        _ingressGate = ingressGate;
        _gatewayClient = gatewayClient;
        _replyClient = replyClient;
        _contentScanner = contentScanner;
        _promptInjectionDetector = promptInjectionDetector
            ?? throw new ArgumentNullException(nameof(promptInjectionDetector));
        _httpClientFactory = httpClientFactory;
        _threadHistoryFetcher = threadHistoryFetcher;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
        _audienceProfiles = toolConfig.AudienceProfiles;
        _modelCapabilities = modelCapabilities;
        _paths = paths;
        _callbackActionStore = callbackActionStore;
    }

    public ChannelType ChannelType => ChannelType.Mattermost;

    public string DisplayName => "Mattermost";

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Degraded, "Mattermost channel disabled."));

        if (_gatewayClient.IsConnected)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Healthy));

        return ValueTask.FromResult(new ChannelHealth(
            ChannelHealthStatus.Disconnected,
            _connectFailureDetail ?? "Mattermost WebSocket disconnected."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Channel disabled by configuration.");
            return;
        }

        // A misconfiguration must never escape StartAsync: an unhandled
        // exception from IHostedService.StartAsync aborts the .NET host and
        // takes the whole daemon down. A misconfigured channel degrades; it
        // does not crash the process (see issue #1033).
        if (string.IsNullOrWhiteSpace(_options.ServerUrl))
        {
            HandleConnectFailure(new ChannelConnectException(
                ChannelConnectFailureKind.Fatal,
                "Mattermost is enabled but Mattermost:ServerUrl is not configured. "
                + "Set the server URL, then restart the daemon."));
            return;
        }

        if (_options.BotToken.IsNullOrEmpty())
        {
            HandleConnectFailure(new ChannelConnectException(
                ChannelConnectFailureKind.Fatal,
                "Mattermost is enabled but no bot token is configured. "
                + "Set the Mattermost:BotToken secret, then restart the daemon."));
            return;
        }

        await TryConnectAsync(_options.ServerUrl, _options.BotToken.Value, cancellationToken);
    }

    private async Task TryConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken)
    {
        try
        {
            // Connect first so BotUserId/BotUsername are available before creating the gateway actor.
            await _gatewayClient.ConnectAsync(serverUrl, botToken, cancellationToken);
            CompleteConnectionSetup(serverUrl);
            _connectFailureDetail = null;
            _logger.LogInformation("Channel connected.");
        }
        catch (Exception ex)
        {
            HandleConnectFailure(MattermostConnectFailureClassifier.Classify(ex));
        }
    }

    /// <summary>
    /// Wires up message handling and the gateway actor once a connection
    /// succeeds. Idempotent — safe to call again after a reconnect.
    /// </summary>
    private void CompleteConnectionSetup(string serverUrl)
    {
        if (_gateway is not null)
            return;

        _gatewayClient.MessageReceived += HandleMessageReceivedAsync;

        var httpClient = _httpClientFactory.CreateClient("mattermost-files");

        _gateway = _system.ActorOf(
            MattermostGatewayActor.CreateProps(new MattermostGatewayDependencies(
                Pipeline: _pipeline,
                IngressGate: _ingressGate,
                TimeProvider: _timeProvider,
                Options: _options,
                DefaultChannelId: DefaultChannelId,
                ReplyClient: _replyClient,
                ContentScanner: _contentScanner,
                AudienceProfiles: _audienceProfiles,
                ModelCapabilities: _modelCapabilities,
                Paths: _paths,
                ServerUrl: serverUrl,
                CallbackUrl: _options.CallbackUrl,
                BotUserId: _gatewayClient.BotUserId,
                BotUsername: _gatewayClient.BotUsername,
                CallbackActionStore: _callbackActionStore,
                PromptInjectionDetector: _promptInjectionDetector,
                ThreadHistoryFetcher: _threadHistoryFetcher,
                HttpClient: httpClient)),
            "mattermost-gateway");

        ActorRegistry.For(_system).Register<MattermostGatewayActorKey>(_gateway);
    }

    private void HandleConnectFailure(ChannelConnectException failure)
    {
        _connectFailureDetail = failure.Message;

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "channel.disconnected",
            AlertType.ChannelDisconnected,
            $"Mattermost channel failed to connect: {failure.Message}",
            AlertSeverity.Warning,
            source: "mattermost",
            context: new Dictionary<string, string>
            {
                ["channel"] = "mattermost",
                ["failure_kind"] = failure.Kind.ToString(),
            }));

        if (failure.IsFatal)
        {
            // Retrying will not help — the operator must fix the configuration.
            // The rest of the daemon keeps running.
            _logger.LogError(
                failure,
                "Mattermost channel could not connect and will stay offline until the "
                + "configuration is fixed and the daemon is restarted. The rest of the "
                + "daemon is unaffected. {Reason}",
                failure.Message);
            return;
        }

        _logger.LogWarning(
            failure,
            "Mattermost channel could not connect (transient). The daemon will keep running "
            + "and retry the connection in the background. {Reason}",
            failure.Message);
        StartReconnectLoop();
    }

    private void StartReconnectLoop()
    {
        if (_reconnectTask is { IsCompleted: false })
            return;

        _reconnectTask = Task.Run(() => ReconnectLoopAsync(_lifetimeCts.Token));
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        var maxDelay = TimeSpan.FromMinutes(5);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Reset transport state so the retry performs a clean login + connect.
            try
            {
                await _gatewayClient.DisconnectAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Transport reset before reconnect failed; continuing.");
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_options.ServerUrl))
                    throw new ChannelConnectException(
                        ChannelConnectFailureKind.Fatal,
                        "Mattermost is enabled but Mattermost:ServerUrl is not configured.");

                if (_options.BotToken.IsNullOrEmpty())
                    throw new ChannelConnectException(
                        ChannelConnectFailureKind.Fatal,
                        "Mattermost is enabled but no bot token is configured.");

                await _gatewayClient.ConnectAsync(_options.ServerUrl, _options.BotToken.Value, cancellationToken);
                CompleteConnectionSetup(_options.ServerUrl);
                _connectFailureDetail = null;
                _logger.LogInformation("Channel reconnected after a transient failure.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var classified = MattermostConnectFailureClassifier.Classify(ex);
                _connectFailureDetail = classified.Message;

                if (classified.IsFatal)
                {
                    _logger.LogError(
                        classified,
                        "Mattermost reconnect hit a fatal failure; giving up until the daemon "
                        + "is restarted. {Reason}",
                        classified.Message);
                    return;
                }

                _logger.LogWarning(
                    classified,
                    "Mattermost reconnect attempt failed; will retry. {Reason}",
                    classified.Message);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the background reconnect loop before tearing down the transport.
        await _lifetimeCts.CancelAsync();
        if (_reconnectTask is { } reconnectTask)
        {
            try
            {
                await reconnectTask;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reconnect loop ended with an error during shutdown.");
            }
        }

        _gatewayClient.MessageReceived -= HandleMessageReceivedAsync;

        if (_gateway is not null)
        {
            try
            {
                await _gateway.GracefulStop(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gateway actor did not stop gracefully; forcing stop");
                _system.Stop(_gateway);
            }

            _gateway = null;
        }

        await _gatewayClient.DisconnectAsync(cancellationToken);
        if (_gatewayClient is IDisposable disposable)
            disposable.Dispose();
    }

    private Task HandleMessageReceivedAsync(MattermostGatewayMessage message)
    {
        _gateway?.Tell(message);
        return Task.CompletedTask;
    }
}
