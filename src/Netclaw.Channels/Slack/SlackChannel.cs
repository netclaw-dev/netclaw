using Akka.Actor;
using Akka.Pattern;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
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
    private readonly TimeProvider _timeProvider;
    private readonly SlackChannelOptions _options;
    private readonly ILogger<SlackChannel> _logger;

    private IActorRef? _gateway;
    private string? _botUserId;
    private string? _defaultChannelId;
    private volatile bool _connected;

    public SlackChannel(
        SessionPipeline pipeline,
        ActorSystem system,
        ISlackApiClient slack,
        ISlackSocketModeClient socketModeClient,
        ISlackReplyClient replyClient,
        TimeProvider timeProvider,
        SlackChannelOptions options,
        ILogger<SlackChannel> logger)
    {
        _pipeline = pipeline;
        _system = system;
        _slack = slack;
        _socketModeClient = socketModeClient;
        _replyClient = replyClient;
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
        _botUserId = auth.UserId;
        _defaultChannelId = await ResolveDefaultChannelIdAsync(cancellationToken);

        _gateway = _system.ActorOf(
            SlackGatewayActor.CreateProps(new SlackGatewayDependencies(
                Pipeline: _pipeline,
                ActorSystem: _system,
                TimeProvider: _timeProvider,
                Options: _options,
                BotUserId: _botUserId,
                DefaultChannelId: _defaultChannelId,
                ReplyClient: _replyClient)),
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
        _gateway?.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: BuildEventId(
                channelId: slackEvent.Channel,
                eventTs: slackEvent.Ts,
                threadTs: slackEvent.ThreadTs,
                userId: slackEvent.User,
                text: slackEvent.Text),
            ChannelId: slackEvent.Channel,
            ThreadTs: slackEvent.ThreadTs,
            EventTs: slackEvent.Ts,
            UserId: slackEvent.User,
            BotId: slackEvent.BotId,
            Text: slackEvent.Text ?? string.Empty,
            Subtype: slackEvent.Subtype,
            Hidden: slackEvent.Hidden,
            IsDirectMessage: IsDirectConversation(slackEvent.Channel)));

        return Task.CompletedTask;
    }

    public Task Handle(AppMention slackEvent)
    {
        _gateway?.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: BuildEventId(
                channelId: slackEvent.Channel,
                eventTs: slackEvent.Ts,
                threadTs: slackEvent.ThreadTs,
                userId: slackEvent.User,
                text: slackEvent.Text),
            ChannelId: slackEvent.Channel,
            ThreadTs: slackEvent.ThreadTs,
            EventTs: slackEvent.Ts,
            UserId: slackEvent.User,
            BotId: null,
            Text: slackEvent.Text ?? string.Empty,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: IsDirectConversation(slackEvent.Channel)));

        return Task.CompletedTask;
    }

    private static bool IsDirectConversation(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            return false;

        return channelId.StartsWith('D');
    }

    private static string BuildEventId(
        string? channelId,
        string? eventTs,
        string? threadTs,
        string? userId,
        string? text)
    {
        if (!string.IsNullOrWhiteSpace(channelId) && !string.IsNullOrWhiteSpace(eventTs))
            return $"{channelId}:{eventTs}";

        var fallback = string.Join("|", [
            channelId ?? string.Empty,
            threadTs ?? string.Empty,
            eventTs ?? string.Empty,
            userId ?? string.Empty,
            text ?? string.Empty
        ]);

        return fallback;
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
