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

    private IActorRef? _gateway;

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
        _promptInjectionDetector = promptInjectionDetector ?? new NullPromptInjectionDetector();
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

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Degraded, "Discord channel disabled."));

        if (_gatewayClient.IsConnected)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Healthy));

        return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Disconnected, "Discord gateway disconnected."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Discord channel disabled by configuration.");
            return;
        }

        var botToken = _options.BotToken.RequireValid("Discord:BotToken");

        try
        {
            // Connect first so BotUserId is available before creating the gateway actor.
            await _gatewayClient.ConnectAsync(botToken.Value, cancellationToken);

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

            _logger.LogInformation("Discord channel connected.");
        }
        catch (Exception ex)
        {
            _gatewayClient.MessageReceived -= HandleMessageReceivedAsync;
            _gatewayClient.InteractionReceived -= HandleInteractionReceivedAsync;

            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "channel.disconnected",
                AlertType.ChannelDisconnected,
                $"Discord channel failed to connect: {ex.Message}",
                AlertSeverity.Warning,
                source: "discord",
                context: new Dictionary<string, string> { ["channel"] = "discord" }));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
