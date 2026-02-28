using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels.Slack;

public sealed class SlackConversationActor : ReceiveActor
{
    private readonly SlackChannelId _conversationId;
    private readonly SlackGatewayDependencies _dependencies;
    private readonly ILoggingAdapter _log;

    public SlackConversationActor(SlackChannelId conversationId, SlackGatewayDependencies dependencies)
    {
        _conversationId = conversationId;
        _dependencies = dependencies;
        _log = Context.GetLogger()
            .WithContext("Adapter", "slack")
            .WithContext("SlackChannelId", _conversationId);

        Context.SetReceiveTimeout(TimeSpan.FromHours(2));
        Receive<ReceiveTimeout>(_ =>
        {
            _log.Info("Slack conversation idle for 2 hours, passivating");
            Context.Stop(Self);
        });

        Receive<SlackInboundMessage>(message =>
        {
            if (!IsAllowedConversation(message))
            {
                _log.Debug("Ignoring Slack event {0}: channel not allowed", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("channel_not_allowed");
                return;
            }

            if (IsBotMessage(message))
            {
                _log.Debug("Ignoring Slack event {0}: bot/self message", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("bot_message");
                return;
            }

            if (!IsAllowedUser(message))
            {
                _log.Debug("Ignoring Slack event {0}: user not allowed", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("user_not_allowed");
                return;
            }

            var containsMention = ContainsBotMention(message.Text);
            var threadTs = message.ThreadTs ?? SlackThreadTs.FromEventTs(message.EventTs);
            var threadActorName = Uri.EscapeDataString(threadTs.Value);
            var existingThread = Context.Child(threadActorName);
            var threadExists = !existingThread.IsNobody();

            var decision = SlackRoutingPolicy.Evaluate(
                message,
                _dependencies.Options.MentionOnly,
                _dependencies.Options.AllowDirectMessages,
                threadExists,
                containsMention);

            if (decision is SlackRoutingDecision.Ignore)
            {
                _log.Debug("Ignoring Slack event {0}: routing policy decision ignore", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("routing_policy_ignore");
                return;
            }

            if (decision is SlackRoutingDecision.ContinueOnly && !threadExists)
            {
                _log.Debug("Ignoring Slack event {0}: thread continuation requested but no thread actor exists", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("thread_not_initialized");
                return;
            }

            var thread = existingThread.IsNobody()
                ? Context.ActorOf(CreateThreadProps(message.ChannelId, threadTs), threadActorName)
                : existingThread;

            var normalized = NormalizeInboundText(message.Text);
            if (string.IsNullOrWhiteSpace(normalized) && message.Files is not { Count: > 0 })
            {
                _log.Debug("Ignoring Slack event {0}: normalized text is empty and no files", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("empty_text");
                return;
            }

            var sessionId = new SessionId($"{_conversationId.Value}/{threadTs.Value}");
            var log = _log
                .WithContext("SlackThreadTs", threadTs.Value)
                .WithContext("SessionId", sessionId.Value);

            log.Debug("Routing Slack event {0} to session thread actor", message.EventId);

            thread.Forward(new SlackThreadInbound(
                SessionId: sessionId,
                ChannelId: message.ChannelId,
                ThreadTs: threadTs,
                SenderId: message.UserId?.Value ?? "slack-user",
                Text: normalized,
                ReceivedAt: _dependencies.TimeProvider.GetUtcNow(),
                Files: message.Files));
        });
    }

    public static Props CreateProps(SlackChannelId conversationId, SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackConversationActor(conversationId, dependencies));

    private bool IsAllowedConversation(SlackInboundMessage message)
    {
        if (message.IsDirectMessage)
            return _dependencies.Options.AllowDirectMessages;

        var matchesDefaultChannel = _dependencies.DefaultChannelId is not null
            && message.ChannelId == _dependencies.DefaultChannelId.Value;

        var matchesAllowedChannel = _dependencies.Options.AllowedChannelIds
            .Contains(message.ChannelId.Value, StringComparer.Ordinal);

        return matchesDefaultChannel || matchesAllowedChannel;
    }

    private bool IsBotMessage(SlackInboundMessage message)
    {
        if (message.BotId is not null)
            return true;

        if (_dependencies.BotUserId is not null
            && message.UserId == _dependencies.BotUserId)
            return true;

        return false;
    }

    private bool IsAllowedUser(SlackInboundMessage message)
    {
        if (_dependencies.Options.AllowedUserIds.Length == 0)
            return true;

        if (message.UserId is not { } userId)
            return false;

        return _dependencies.Options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
    }

    private bool ContainsBotMention(string text)
    {
        if (_dependencies.BotUserId is not { } botUserId)
            return false;

        return text.Contains($"<@{botUserId.Value}>", StringComparison.Ordinal);
    }

    private string NormalizeInboundText(string text)
    {
        if (_dependencies.BotUserId is not { } botUserId)
            return text.Trim();

        var mention = $"<@{botUserId.Value}>";
        return text.Replace(mention, string.Empty, StringComparison.Ordinal).Trim();
    }

    private Props CreateThreadProps(SlackChannelId channelId, SlackThreadTs threadTs)
    {
        var sessionId = new SessionId($"{_conversationId.Value}/{threadTs.Value}");

        if (_dependencies.ThreadPropsFactory is not null)
            return _dependencies.ThreadPropsFactory(sessionId, channelId, threadTs, _dependencies);

        return SlackThreadBindingActor.CreateProps(
            sessionId: sessionId,
            channelId: channelId,
            threadTs: threadTs,
            dependencies: _dependencies);
    }
}
