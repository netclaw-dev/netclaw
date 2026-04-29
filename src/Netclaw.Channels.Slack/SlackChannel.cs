// -----------------------------------------------------------------------
// <copyright file="SlackChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack;

public sealed class SlackChannel : IChannel, IEventHandler<MessageEvent>, IEventHandler<AppMention>
{
    private readonly ISessionPipeline _pipeline;
    private readonly ActorSystem _system;
    private readonly ISlackApiClient _slack;
    private readonly ISlackSocketModeClient _socketModeClient;
    private readonly ISlackReplyClient _replyClient;
    private readonly SessionIngressGate _ingressGate;
    private readonly IContentScanner _contentScanner;
    private readonly IPromptInjectionDetector _promptInjectionDetector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly SlackChannelOptions _options;
    private readonly ILogger<SlackChannel> _logger;
    private readonly IThreadHistoryFetcher _threadHistoryFetcher;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly NetclawPaths _paths;

    private IActorRef? _gateway;
    private SlackUserId? _botUserId;
    private SlackChannelId? _defaultChannelId;
    private volatile bool _connected;

    public SlackChannel(
        ISessionPipeline pipeline,
        ActorSystem system,
        ISlackApiClient slack,
        ISlackSocketModeClient socketModeClient,
        ISlackReplyClient replyClient,
        SessionIngressGate ingressGate,
        IContentScanner contentScanner,
        IPromptInjectionDetector? promptInjectionDetector,
        IHttpClientFactory httpClientFactory,
        IOperationalNotificationSink notificationSink,
        TimeProvider timeProvider,
        SlackChannelOptions options,
        ILogger<SlackChannel> logger,
        IThreadHistoryFetcher threadHistoryFetcher,
        ToolConfig toolConfig,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths)
    {
        _pipeline = pipeline;
        _system = system;
        _slack = slack;
        _socketModeClient = socketModeClient;
        _replyClient = replyClient;
        _ingressGate = ingressGate;
        _contentScanner = contentScanner;
        _promptInjectionDetector = promptInjectionDetector ?? new NullPromptInjectionDetector();
        _httpClientFactory = httpClientFactory;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
        _threadHistoryFetcher = threadHistoryFetcher ?? throw new ArgumentNullException(nameof(threadHistoryFetcher));
        _audienceProfiles = toolConfig.AudienceProfiles;
        _modelCapabilities = modelCapabilities;
        _paths = paths;
    }

    public Actors.Channels.ChannelType ChannelType => Actors.Channels.ChannelType.Slack;

    public string DisplayName => "Slack";

    /// <summary>
    /// The gateway actor ref, exposed so that proactive tools can send
    /// <see cref="StartProactiveThread"/> messages to wire up the actor hierarchy.
    /// </summary>
    internal IActorRef? Gateway => _gateway;

    /// <summary>
    /// The resolved default channel ID, available after <see cref="StartAsync"/> completes.
    /// Exposed for proactive tools that need runtime-resolved channel IDs for ACL checks.
    /// </summary>
    internal SlackChannelId? DefaultChannelId => _defaultChannelId;

    internal void HandleApprovalResponse(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string callId,
        string selectedKey,
        string senderId,
        string? requesterSenderId)
    {
        _gateway?.Tell(new SlackApprovalResponse(
            channelId,
            threadTs,
            callId,
            selectedKey,
            senderId,
            requesterSenderId));
    }

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Degraded, "Slack channel disabled."));

        if (_connected)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Healthy));

        return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Disconnected, "Slack socket mode disconnected."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Slack channel disabled by configuration.");
            return;
        }

        if (!_options.SocketMode)
            throw new InvalidOperationException("Slack channel currently supports Socket Mode only.");

        try
        {
            var auth = await _slack.Auth.Test(cancellationToken);
            _botUserId = !string.IsNullOrWhiteSpace(auth.UserId) ? new SlackUserId(auth.UserId) : null;
            var resolvedChannelId = await ResolveDefaultChannelIdAsync(cancellationToken);
            _defaultChannelId = resolvedChannelId is not null ? new SlackChannelId(resolvedChannelId) : null;

            var httpClient = _httpClientFactory.CreateClient("slack-files");

            _gateway = _system.ActorOf(
                SlackGatewayActor.CreateProps(new SlackGatewayDependencies(
                    Pipeline: _pipeline,
                    IngressGate: _ingressGate,
                    ActorSystem: _system,
                    TimeProvider: _timeProvider,
                    Options: _options,
                    BotUserId: _botUserId,
                    DefaultChannelId: _defaultChannelId,
                    ReplyClient: _replyClient,
                    ContentScanner: _contentScanner,
                    ThreadHistoryFetcher: _threadHistoryFetcher,
                    AudienceProfiles: _audienceProfiles,
                    ModelCapabilities: _modelCapabilities,
                    Paths: _paths,
                    HttpClient: httpClient,
                    PromptInjectionDetector: _promptInjectionDetector)),
                "slack-gateway");

            // Publish the gateway under SlackGatewayActorKey so the reminder
            // dispatcher can resolve it via IRequiredActor<SlackGatewayActorKey>
            // for Mode B DeliverTrustedSessionTurn delivery.
            ActorRegistry.For(_system).Register<SlackGatewayActorKey>(_gateway);

            await _socketModeClient.Connect(cancellationToken: cancellationToken);
            _connected = true;

            _logger.LogInformation("Slack channel connected as user {BotUserId}.", _botUserId);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "channel.disconnected",
                AlertType.ChannelDisconnected,
                $"Slack channel failed to connect: {ex.Message}",
                AlertSeverity.Warning,
                source: "slack",
                context: new Dictionary<string, string> { ["channel"] = "slack" }));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _connected = false;
        _socketModeClient.Disconnect();

        if (_gateway is not null)
        {
            try
            {
                await _gateway.GracefulStop(TimeSpan.FromSeconds(5));
            }
            catch
            {
                _system.Stop(_gateway);
            }

            _gateway = null;
        }
    }

    public Task Handle(MessageEvent slackEvent)
    {
        ForwardInboundMessage(
            kind: SlackInboundKind.Message,
            channel: slackEvent.Channel,
            eventTs: slackEvent.Ts,
            threadTs: slackEvent.ThreadTs,
            userId: slackEvent.User,
            botId: slackEvent.BotId,
            text: slackEvent.Text,
            subtype: slackEvent.Subtype,
            hidden: slackEvent.Hidden,
            files: slackEvent.Files);
        return Task.CompletedTask;
    }

    public Task Handle(AppMention slackEvent)
    {
        ForwardInboundMessage(
            kind: SlackInboundKind.AppMention,
            channel: slackEvent.Channel,
            eventTs: slackEvent.Ts,
            threadTs: slackEvent.ThreadTs,
            userId: slackEvent.User,
            botId: null,
            text: slackEvent.Text,
            subtype: null,
            hidden: false,
            files: slackEvent.Files);
        return Task.CompletedTask;
    }

    private void ForwardInboundMessage(
        SlackInboundKind kind,
        string channel,
        string eventTs,
        string? threadTs,
        string? userId,
        string? botId,
        string? text,
        string? subtype,
        bool hidden,
        IList<SlackNet.File>? files)
    {
        var mappedFiles = MapSlackFiles(files);
        var channelId = new SlackChannelId(channel);

        _gateway?.Tell(new SlackInboundMessage(
            Kind: kind,
            EventId: BuildEventId(
                channelId: channel,
                eventTs: eventTs,
                threadTs: threadTs,
                userId: userId,
                text: text),
            ChannelId: channelId,
            ThreadTs: !string.IsNullOrWhiteSpace(threadTs) ? new SlackThreadTs(threadTs) : null,
            EventTs: new SlackEventTs(eventTs),
            UserId: !string.IsNullOrWhiteSpace(userId) ? new SlackUserId(userId) : null,
            BotId: !string.IsNullOrWhiteSpace(botId) ? new SlackBotId(botId) : null,
            Text: text ?? string.Empty,
            Subtype: subtype,
            Hidden: hidden,
            IsDirectMessage: IsDirectConversation(channelId),
            Files: mappedFiles));
    }

    private static IReadOnlyList<SlackFileReference>? MapSlackFiles(IList<SlackNet.File>? files)
    {
        if (files is null or { Count: 0 })
            return null;

        var result = new List<SlackFileReference>(files.Count);
        foreach (var f in files)
        {
            var downloadUrl = f.UrlPrivateDownload ?? f.UrlPrivate;
            if (string.IsNullOrWhiteSpace(downloadUrl))
                continue;

            result.Add(new SlackFileReference(
                Id: f.Id ?? string.Empty,
                Name: f.Name ?? "attachment",
                MimeType: f.Mimetype ?? "application/octet-stream",
                Size: f.Size,
                UrlPrivateDownload: downloadUrl));
        }

        return result.Count > 0 ? result : null;
    }

    private static bool IsDirectConversation(SlackChannelId channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId.Value))
            return false;

        return channelId.Value.StartsWith('D');
    }

    private static SlackEventId BuildEventId(
        string? channelId,
        string? eventTs,
        string? threadTs,
        string? userId,
        string? text)
    {
        if (!string.IsNullOrWhiteSpace(channelId) && !string.IsNullOrWhiteSpace(eventTs))
            return new SlackEventId($"{channelId}:{eventTs}");

        var fallback = string.Join("|", [
            channelId ?? string.Empty,
            threadTs ?? string.Empty,
            eventTs ?? string.Empty,
            userId ?? string.Empty,
            text ?? string.Empty
        ]);

        return new SlackEventId(fallback);
    }

    private async Task<string?> ResolveDefaultChannelIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.DefaultChannelId))
            return _options.DefaultChannelId;

        if (string.IsNullOrWhiteSpace(_options.DefaultChannelName))
            return null;

        var cursor = default(string);
        do
        {
            var page = await _slack.Conversations.List(
                types: [ConversationType.PublicChannel, ConversationType.PrivateChannel],
                cursor: cursor,
                cancellationToken: cancellationToken);

            var match = page.Channels.FirstOrDefault(c =>
                string.Equals(c.Name, _options.DefaultChannelName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.NameNormalized, _options.DefaultChannelName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match.Id;

            cursor = page.ResponseMetadata?.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        _logger.LogWarning("Could not resolve Slack channel name '{ChannelName}'. Listening on all channels.", _options.DefaultChannelName);
        return null;
    }
}
