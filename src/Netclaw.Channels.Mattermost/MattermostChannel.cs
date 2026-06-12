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

    private readonly object _connectionSetupLock = new();
    private volatile IActorRef? _gateway;
    private volatile string? _connectFailureDetail;

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

        _gatewayClient.CleanReconnectRequired += HandleCleanReconnectRequiredAsync;
        _gatewayClient.ConnectionRestored += HandleConnectionRestoredAsync;
    }

    public ChannelType ChannelType => ChannelType.Mattermost;

    public string DisplayName => "Mattermost";

    public async ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return new ChannelHealth(ChannelHealthStatus.Degraded, "Mattermost channel disabled.");

        var gatewaySnapshot = await _gatewayClient.GetSnapshotAsync(cancellationToken);
        return GatewayChannelHealth.Evaluate(
            gatewaySnapshot,
            _connectFailureDetail,
            notReadyFallback: "Mattermost gateway connected but not ready.",
            disconnectedFallback: "Mattermost WebSocket disconnected.");
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
            var gatewaySnapshot = await _gatewayClient.ConnectAsync(serverUrl, botToken, cancellationToken);
            EnsureGatewayReadyAfterConnect(gatewaySnapshot);
            CompleteConnectionSetup(serverUrl, gatewaySnapshot.BotUserId, gatewaySnapshot.BotUsername);
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
    /// succeeds. Idempotent and thread-safe: ConnectionRestored publishes on
    /// every transition to Ready, so a normal operator connect reaches this
    /// from both the connect-ask continuation and the event handler — the
    /// lock + guard make the setup exactly-once (no duplicate gateway actor,
    /// no double event subscription).
    /// </summary>
    private void CompleteConnectionSetup(
        string serverUrl,
        MattermostUserId? botUserId,
        string? botUsername)
    {
        lock (_connectionSetupLock)
        {
            if (_gateway is not null)
                return;

            CompleteConnectionSetupCore(serverUrl, botUserId, botUsername);
        }
    }

    private void CompleteConnectionSetupCore(
        string serverUrl,
        MattermostUserId? botUserId,
        string? botUsername)
    {
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
                BotUserId: botUserId,
                BotUsername: botUsername,
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
            _logger.LogError(
                failure,
                "Mattermost channel could not connect and will stay offline until the "
                + "configuration is fixed and the daemon is restarted. The rest of the "
                + "daemon is unaffected. {Reason}",
                failure.Message);
            return;
        }

        // Transient failures are retried by the lifecycle actor's built-in
        // auto-reconnect. The channel just logs and waits for the
        // ConnectionRestored event.
        _logger.LogWarning(
            failure,
            "Mattermost channel could not connect (transient). The lifecycle actor will "
            + "retry the connection in the background. {Reason}",
            failure.Message);
    }

    private Task HandleCleanReconnectRequiredAsync(string reason)
    {
        _connectFailureDetail = reason;
        _logger.LogWarning("Gateway requested clean reconnect: {Reason}", reason);
        return Task.CompletedTask;
    }

    private Task HandleConnectionRestoredAsync(MattermostGatewaySnapshot snapshot)
    {
        _connectFailureDetail = null;
        CompleteConnectionSetup(
            _options.ServerUrl!,
            snapshot.BotUserId,
            snapshot.BotUsername);
        _logger.LogInformation("Gateway connection ready; channel setup ensured.");
        return Task.CompletedTask;
    }

    private static void EnsureGatewayReadyAfterConnect(MattermostGatewaySnapshot gatewaySnapshot)
    {
        if (gatewaySnapshot.IsReady)
            return;

        throw new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            gatewaySnapshot.HealthDetail ?? "Mattermost gateway connected but did not become ready.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gatewayClient.CleanReconnectRequired -= HandleCleanReconnectRequiredAsync;
        _gatewayClient.ConnectionRestored -= HandleConnectionRestoredAsync;
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
