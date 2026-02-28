using Akka.Actor;
using Akka.Pattern;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Security;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack;

public sealed class SlackChannel : IChannel, IEventHandler<MessageEvent>, IEventHandler<AppMention>
{
    private readonly SessionPipeline _pipeline;
    private readonly ActorSystem _system;
    private readonly ISlackApiClient _slack;
    private readonly ISlackSocketModeClient _socketModeClient;
    private readonly ISlackReplyClient _replyClient;
    private readonly IContentScanner _contentScanner;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SlackChannelOptions _options;
    private readonly ILogger<SlackChannel> _logger;

    private IActorRef? _gateway;
    private SlackUserId? _botUserId;
    private SlackChannelId? _defaultChannelId;
    private volatile bool _connected;

    public SlackChannel(
        SessionPipeline pipeline,
        ActorSystem system,
        ISlackApiClient slack,
        ISlackSocketModeClient socketModeClient,
        ISlackReplyClient replyClient,
        IContentScanner contentScanner,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        SlackChannelOptions options,
        ILogger<SlackChannel> logger)
    {
        _pipeline = pipeline;
        _system = system;
        _slack = slack;
        _socketModeClient = socketModeClient;
        _replyClient = replyClient;
        _contentScanner = contentScanner;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public string ChannelType => "slack";

    public string DisplayName => "Slack";

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

        var auth = await _slack.Auth.Test(cancellationToken);
        _botUserId = !string.IsNullOrWhiteSpace(auth.UserId) ? new SlackUserId(auth.UserId) : null;
        var resolvedChannelId = await ResolveDefaultChannelIdAsync(cancellationToken);
        _defaultChannelId = resolvedChannelId is not null ? new SlackChannelId(resolvedChannelId) : null;

        var httpClient = _httpClientFactory.CreateClient("slack-files");

        _gateway = _system.ActorOf(
            SlackGatewayActor.CreateProps(new SlackGatewayDependencies(
                Pipeline: _pipeline,
                ActorSystem: _system,
                TimeProvider: _timeProvider,
                Options: _options,
                BotUserId: _botUserId,
                DefaultChannelId: _defaultChannelId,
                ReplyClient: _replyClient,
                ContentScanner: _contentScanner,
                HttpClient: httpClient)),
            "slack-gateway");

        await _socketModeClient.Connect(cancellationToken: cancellationToken);
        _connected = true;

        _logger.LogInformation("Slack channel connected as user {BotUserId}.", _botUserId);
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
        // Map Slack file attachments to SlackFileReference
        var files = MapSlackFiles(slackEvent.Files);
        var channelId = new SlackChannelId(slackEvent.Channel);

        _gateway?.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: BuildEventId(
                channelId: slackEvent.Channel,
                eventTs: slackEvent.Ts,
                threadTs: slackEvent.ThreadTs,
                userId: slackEvent.User,
                text: slackEvent.Text),
            ChannelId: channelId,
            ThreadTs: !string.IsNullOrWhiteSpace(slackEvent.ThreadTs) ? new SlackThreadTs(slackEvent.ThreadTs) : null,
            EventTs: new SlackEventTs(slackEvent.Ts),
            UserId: !string.IsNullOrWhiteSpace(slackEvent.User) ? new SlackUserId(slackEvent.User) : null,
            BotId: !string.IsNullOrWhiteSpace(slackEvent.BotId) ? new SlackBotId(slackEvent.BotId) : null,
            Text: slackEvent.Text ?? string.Empty,
            Subtype: slackEvent.Subtype,
            Hidden: slackEvent.Hidden,
            IsDirectMessage: IsDirectConversation(channelId),
            Files: files));

        return Task.CompletedTask;
    }

    public Task Handle(AppMention slackEvent)
    {
        var files = MapSlackFiles(slackEvent.Files);
        var channelId = new SlackChannelId(slackEvent.Channel);

        _gateway?.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: BuildEventId(
                channelId: slackEvent.Channel,
                eventTs: slackEvent.Ts,
                threadTs: slackEvent.ThreadTs,
                userId: slackEvent.User,
                text: slackEvent.Text),
            ChannelId: channelId,
            ThreadTs: !string.IsNullOrWhiteSpace(slackEvent.ThreadTs) ? new SlackThreadTs(slackEvent.ThreadTs) : null,
            EventTs: new SlackEventTs(slackEvent.Ts),
            UserId: !string.IsNullOrWhiteSpace(slackEvent.User) ? new SlackUserId(slackEvent.User) : null,
            BotId: null,
            Text: slackEvent.Text ?? string.Empty,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: IsDirectConversation(channelId),
            Files: files));

        return Task.CompletedTask;
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
