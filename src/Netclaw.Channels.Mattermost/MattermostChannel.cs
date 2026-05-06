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
    private readonly byte[]? _callbackSigningKey;

    private IActorRef? _gateway;

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
        MattermostCallbackSigningKey? callbackSigningKey = null)
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
        _callbackSigningKey = callbackSigningKey?.Key;
    }

    public ChannelType ChannelType => ChannelType.Mattermost;

    public string DisplayName => "Mattermost";

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Degraded, "Mattermost channel disabled."));

        if (_gatewayClient.IsConnected)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Healthy));

        return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Disconnected, "Mattermost WebSocket disconnected."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Mattermost channel disabled by configuration.");
            return;
        }

        var serverUrl = _options.ServerUrl
            ?? throw new InvalidOperationException("Mattermost:ServerUrl is required when Mattermost channel is enabled.");

        try
        {
            await _gatewayClient.ConnectAsync(serverUrl, _options.BotToken!.Value, cancellationToken);

            _gatewayClient.MessageReceived += HandleMessageReceivedAsync;
            _gatewayClient.InteractionReceived += HandleInteractionReceivedAsync;

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
                    CallbackSigningKey: _callbackSigningKey,
                    PromptInjectionDetector: _promptInjectionDetector,
                    ThreadHistoryFetcher: _threadHistoryFetcher,
                    HttpClient: httpClient)),
                "mattermost-gateway");

            ActorRegistry.For(_system).Register<MattermostGatewayActorKey>(_gateway);

            _logger.LogInformation("Mattermost channel connected.");
        }
        catch (Exception ex)
        {
            _gatewayClient.MessageReceived -= HandleMessageReceivedAsync;
            _gatewayClient.InteractionReceived -= HandleInteractionReceivedAsync;

            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "channel.disconnected",
                AlertType.ChannelDisconnected,
                $"Mattermost channel failed to connect: {ex.Message}",
                AlertSeverity.Warning,
                source: "mattermost",
                context: new Dictionary<string, string> { ["channel"] = "mattermost" }));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gatewayClient.MessageReceived -= HandleMessageReceivedAsync;
        _gatewayClient.InteractionReceived -= HandleInteractionReceivedAsync;

        if (_gateway is not null)
        {
            try
            {
                await _gateway.GracefulStop(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mattermost gateway actor did not stop gracefully; forcing stop");
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

    private Task HandleInteractionReceivedAsync(MattermostGatewayInteraction interaction)
    {
        _gateway?.Tell(interaction);
        return Task.CompletedTask;
    }
}
