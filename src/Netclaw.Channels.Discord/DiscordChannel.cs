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

    // Cancels the background reconnect loop when the channel stops.
    private readonly CancellationTokenSource _lifetimeCts = new();

    private IActorRef? _gateway;
    private Task? _reconnectTask;
    private volatile string? _connectFailureDetail;

    public DiscordChannel(
        ActorSystem system,
        ISessionPipeline pipeline,
        SessionIngressGate ingressGate,
        IDiscordGatewayClient gatewayClient,
        IDiscordReplyClient replyClient,
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
    }

    public ChannelType ChannelType => ChannelType.Discord;

    public string DisplayName => "Discord";

    /// <summary>
    /// The gateway actor ref, exposed so that proactive tools can send
    /// <see cref="StartProactiveThread"/> messages to wire up the actor
    /// hierarchy. Null until a connection succeeds.
    /// </summary>
    internal IActorRef? Gateway => _gateway;

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Degraded, "Discord channel disabled."));

        if (_gatewayClient.IsConnected)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Healthy));

        return ValueTask.FromResult(new ChannelHealth(
            ChannelHealthStatus.Disconnected,
            _connectFailureDetail ?? "Discord gateway disconnected."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Discord channel disabled by configuration.");
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
            await _gatewayClient.ConnectAsync(botToken, cancellationToken);
            CompleteConnectionSetup();
            _connectFailureDetail = null;
            _logger.LogInformation("Discord channel connected.");
        }
        catch (Exception ex)
        {
            HandleConnectFailure(DiscordConnectFailureClassifier.Classify(ex));
        }
    }

    /// <summary>
    /// Wires up message handling and the gateway actor once a connection
    /// succeeds. Idempotent — safe to call again after a reconnect.
    /// </summary>
    private void CompleteConnectionSetup()
    {
        if (_gateway is not null)
            return;

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
                ReplyClient: _replyClient,
                ContentScanner: _contentScanner,
                AudienceProfiles: _audienceProfiles,
                ModelCapabilities: _modelCapabilities,
                Paths: _paths,
                BotUserId: _gatewayClient.BotUserId,
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
            // Retrying will not help — the operator must fix the configuration.
            // The rest of the daemon keeps running.
            _logger.LogError(
                failure,
                "Discord channel could not connect and will stay offline until the "
                + "configuration is fixed and the daemon is restarted. The rest of the "
                + "daemon is unaffected. {Reason}",
                failure.Message);
            return;
        }

        _logger.LogWarning(
            failure,
            "Discord channel could not connect (transient). The daemon will keep running "
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
                _logger.LogDebug(ex, "Discord transport reset before reconnect failed; continuing.");
            }

            try
            {
                if (_options.BotToken.IsNullOrEmpty())
                    throw new ChannelConnectException(
                        ChannelConnectFailureKind.Fatal,
                        "Discord is enabled but no bot token is configured.");

                await _gatewayClient.ConnectAsync(_options.BotToken.Value, cancellationToken);
                CompleteConnectionSetup();
                _connectFailureDetail = null;
                _logger.LogInformation("Discord channel reconnected after a transient failure.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var classified = DiscordConnectFailureClassifier.Classify(ex);
                _connectFailureDetail = classified.Message;

                if (classified.IsFatal)
                {
                    _logger.LogError(
                        classified,
                        "Discord reconnect hit a fatal failure; giving up until the daemon "
                        + "is restarted. {Reason}",
                        classified.Message);
                    return;
                }

                _logger.LogWarning(
                    classified,
                    "Discord reconnect attempt failed; will retry. {Reason}",
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
                _logger.LogDebug(ex, "Discord reconnect loop ended with an error during shutdown.");
            }
        }

        // Unsubscribe events first so no new messages enter the actor system.
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
                _logger.LogWarning(ex, "Discord gateway actor did not stop gracefully; forcing stop");
                _system.Stop(_gateway);
            }

            _gateway = null;
        }

        await _gatewayClient.DisconnectAsync(cancellationToken);
        if (_gatewayClient is IDisposable disposable)
            disposable.Dispose();

        _lifetimeCts.Dispose();
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
