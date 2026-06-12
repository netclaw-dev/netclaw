// -----------------------------------------------------------------------
// <copyright file="DiscordChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Channels;
using Netclaw.Channels.Discord.Transport;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Discord;

public sealed class DiscordChannel : IChannel
{
    private readonly ActorSystem _system;
    private readonly ISessionPipeline _pipeline;
    private readonly SessionIngressGate _ingressGate;
    private readonly IDiscordGatewayClient _gatewayClient;
    private readonly IDiscordReplyClient _replyClient;
    private readonly IChannelRegistry _channelRegistry;
    private readonly IContentScanner _contentScanner;
    private readonly IPromptInjectionDetector _promptInjectionDetector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThreadHistoryFetcher? _threadHistoryFetcher;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly DiscordChannelOptions _options;
    private readonly ILogger<DiscordChannel> _logger;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly NetclawPaths _paths;

    private readonly object _connectionSetupLock = new();
    private volatile IActorRef? _gateway;
    private volatile string? _connectFailureDetail;

    public DiscordChannel(
        ActorSystem system,
        ISessionPipeline pipeline,
        SessionIngressGate ingressGate,
        IDiscordGatewayClient gatewayClient,
        IDiscordReplyClient replyClient,
        IChannelRegistry channelRegistry,
        IContentScanner contentScanner,
        IPromptInjectionDetector? promptInjectionDetector,
        IHttpClientFactory httpClientFactory,
        IThreadHistoryFetcher? threadHistoryFetcher,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        DiscordChannelOptions options,
        ILogger<DiscordChannel> logger,
        ToolConfig toolConfig,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths)
    {
        _system = system;
        _pipeline = pipeline;
        _ingressGate = ingressGate;
        _gatewayClient = gatewayClient;
        _replyClient = replyClient;
        _channelRegistry = channelRegistry;
        _contentScanner = contentScanner;
        // Fail loud rather than substituting a no-op detector — a no-op reports
        // every input as safe, silently disabling injection scanning. A null
        // here means broken DI wiring.
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

        _gatewayClient.CleanReconnectRequired += HandleCleanReconnectRequiredAsync;
        _gatewayClient.ConnectionRestored += HandleConnectionRestoredAsync;
    }

    public ChannelType ChannelType => ChannelType.Discord;

    public string DisplayName => "Discord";

    /// <summary>
    /// The gateway actor ref, exposed so that proactive tools can send
    /// <see cref="StartProactiveThread"/> messages to wire up the actor
    /// hierarchy. Null until a connection succeeds.
    /// </summary>
    internal IActorRef? Gateway => _gateway;

    public async ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return new ChannelHealth(ChannelHealthStatus.Degraded, "Discord channel disabled.");

        var gatewaySnapshot = await _gatewayClient.GetSnapshotAsync(cancellationToken);
        return GatewayChannelHealth.Evaluate(
            gatewaySnapshot,
            _connectFailureDetail,
            notReadyFallback: "Discord gateway connected but not ready.",
            disconnectedFallback: "Discord gateway disconnected.");
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
        // does not crash the process.
        if (_options.BotToken.IsNullOrEmpty())
        {
            HandleConnectFailure(new ChannelConnectException(
                ChannelConnectFailureKind.Fatal,
                "Discord is enabled but no bot token is configured. "
                + "Set the Discord:BotToken secret, then restart the daemon."));
            return;
        }

        await TryConnectAsync(_options.BotToken.Value, cancellationToken);
    }

    private async Task TryConnectAsync(string botToken, CancellationToken cancellationToken)
    {
        try
        {
            // Connect first so BotUserId is available before creating the gateway actor.
            var gatewaySnapshot = await _gatewayClient.ConnectAsync(botToken, cancellationToken);
            EnsureGatewayReadyAfterConnect(gatewaySnapshot);
            CompleteConnectionSetup(gatewaySnapshot.BotUserId);
            _connectFailureDetail = null;
            _logger.LogInformation("Channel connected.");
        }
        catch (Exception ex)
        {
            HandleConnectFailure(DiscordConnectFailureClassifier.Classify(ex));
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
    private void CompleteConnectionSetup(DiscordUserId? botUserId)
    {
        lock (_connectionSetupLock)
        {
            if (_gateway is not null)
                return;

            CompleteConnectionSetupCore(botUserId);
        }
    }

    private void CompleteConnectionSetupCore(DiscordUserId? botUserId)
    {
        _gatewayClient.MessageReceived += HandleMessageReceivedAsync;
        _gatewayClient.InteractionReceived += HandleInteractionReceivedAsync;

        var httpClient = _httpClientFactory.CreateClient("discord-files");

        _gateway = _system.ActorOf(
            DiscordGatewayActor.CreateProps(new DiscordGatewayDependencies(
                Pipeline: _pipeline,
                IngressGate: _ingressGate,
                TimeProvider: _timeProvider,
                Options: _options,
                DefaultChannelId: !string.IsNullOrWhiteSpace(_options.DefaultChannelId)
                    ? new DiscordChannelId(_options.DefaultChannelId)
                    : null,
                ChannelRegistry: _channelRegistry,
                ReplyClient: _replyClient,
                ContentScanner: _contentScanner,
                AudienceProfiles: _audienceProfiles,
                ModelCapabilities: _modelCapabilities,
                Paths: _paths,
                BotUserId: botUserId,
                PromptInjectionDetector: _promptInjectionDetector,
                ThreadHistoryFetcher: _threadHistoryFetcher,
                HttpClient: httpClient)),
            "discord-gateway");

        ActorRegistry.For(_system).Register<DiscordGatewayActorKey>(_gateway);
    }

    private void HandleConnectFailure(ChannelConnectException failure)
    {
        _connectFailureDetail = failure.Message;

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "channel.disconnected",
            AlertType.ChannelDisconnected,
            $"Discord channel failed to connect: {failure.Message}",
            AlertSeverity.Warning,
            source: "discord",
            context: new Dictionary<string, string>
            {
                ["channel"] = "discord",
                ["failure_kind"] = failure.Kind.ToString(),
            }));

        if (failure.IsFatal)
        {
            _logger.LogError(
                failure,
                "Discord channel could not connect and will stay offline until the "
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
            "Discord channel could not connect (transient). The lifecycle actor will "
            + "retry the connection in the background. {Reason}",
            failure.Message);
    }

    private Task HandleCleanReconnectRequiredAsync(string reason)
    {
        _connectFailureDetail = reason;
        _logger.LogWarning("Gateway requested clean reconnect: {Reason}", reason);
        return Task.CompletedTask;
    }

    private Task HandleConnectionRestoredAsync(DiscordGatewaySnapshot snapshot)
    {
        _connectFailureDetail = null;
        CompleteConnectionSetup(snapshot.BotUserId);
        _logger.LogInformation("Gateway connection ready; channel setup ensured.");
        return Task.CompletedTask;
    }

    private static void EnsureGatewayReadyAfterConnect(DiscordGatewaySnapshot gatewaySnapshot)
    {
        if (gatewaySnapshot.IsReady)
            return;

        throw new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            gatewaySnapshot.HealthDetail ?? "Discord gateway connected but did not become ready.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gatewayClient.CleanReconnectRequired -= HandleCleanReconnectRequiredAsync;
        _gatewayClient.ConnectionRestored -= HandleConnectionRestoredAsync;
        _gatewayClient.MessageReceived -= HandleMessageReceivedAsync;
        _gatewayClient.InteractionReceived -= HandleInteractionReceivedAsync;

        // Drain actors before disconnecting the transport client so that
        // in-flight replies can still reach Discord.
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

    private Task HandleMessageReceivedAsync(DiscordGatewayMessage message)
    {
        _gateway?.Tell(message);
        return Task.CompletedTask;
    }

    private Task HandleInteractionReceivedAsync(DiscordGatewayInteraction interaction)
    {
        _gateway?.Tell(interaction);
        return Task.CompletedTask;
    }
}
