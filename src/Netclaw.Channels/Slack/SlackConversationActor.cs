using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels.Slack;

public sealed class SlackConversationActor : ReceiveActor
{
    private readonly string _conversationId;
    private readonly SlackGatewayDependencies _dependencies;
    private readonly ILoggingAdapter _log;

    public SlackConversationActor(string conversationId, SlackGatewayDependencies dependencies)
    {
        _conversationId = conversationId;
        _dependencies = dependencies;
        _log = Context.GetLogger()
            .WithContext("Adapter", "slack")
            .WithContext("SlackChannelId", _conversationId);

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
            var threadTs = string.IsNullOrWhiteSpace(message.ThreadTs) ? message.EventTs : message.ThreadTs!;
            var threadActorName = Uri.EscapeDataString(threadTs);
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
            if (string.IsNullOrWhiteSpace(normalized))
            {
                _log.Debug("Ignoring Slack event {0}: normalized text is empty", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("empty_text");
                return;
            }

            var sessionId = new SessionId($"{_conversationId}/{threadTs}");
            var log = _log
                .WithContext("SlackThreadTs", threadTs)
                .WithContext("SessionId", sessionId.Value);

            log.Debug("Routing Slack event {0} to session thread actor", message.EventId);

            thread.Forward(new SlackThreadInbound(
                SessionId: sessionId,
                ChannelId: message.ChannelId,
                ThreadTs: threadTs,
                SenderId: message.UserId ?? "slack-user",
                Text: normalized,
                ReceivedAt: _dependencies.TimeProvider.GetUtcNow()));
        });
    }

    public static Props CreateProps(string conversationId, SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackConversationActor(conversationId, dependencies));

    private bool IsAllowedConversation(SlackInboundMessage message)
    {
        if (message.IsDirectMessage)
            return _dependencies.Options.AllowDirectMessages;

        if (!string.IsNullOrWhiteSpace(_dependencies.DefaultChannelId)
            && !string.Equals(message.ChannelId, _dependencies.DefaultChannelId, StringComparison.Ordinal))
            return false;

        if (_dependencies.Options.AllowedChannelIds is { Length: > 0 }
            && !_dependencies.Options.AllowedChannelIds.Contains(message.ChannelId, StringComparer.Ordinal))
            return false;

        return true;
    }

    private bool IsBotMessage(SlackInboundMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.BotId))
            return true;

        if (!string.IsNullOrWhiteSpace(_dependencies.BotUserId)
            && string.Equals(message.UserId, _dependencies.BotUserId, StringComparison.Ordinal))
            return true;

        return false;
    }

    private bool IsAllowedUser(SlackInboundMessage message)
    {
        if (_dependencies.Options.AllowedUserIds is not { Length: > 0 })
            return true;

        if (string.IsNullOrWhiteSpace(message.UserId))
            return false;

        return _dependencies.Options.AllowedUserIds.Contains(message.UserId, StringComparer.Ordinal);
    }

    private bool ContainsBotMention(string text)
    {
        if (string.IsNullOrWhiteSpace(_dependencies.BotUserId))
            return false;

        return text.Contains($"<@{_dependencies.BotUserId}>", StringComparison.Ordinal);
    }

    private string NormalizeInboundText(string text)
    {
        if (string.IsNullOrWhiteSpace(_dependencies.BotUserId))
            return text.Trim();

        var mention = $"<@{_dependencies.BotUserId}>";
        return text.Replace(mention, string.Empty, StringComparison.Ordinal).Trim();
    }

    private Props CreateThreadProps(string channelId, string threadTs)
    {
        var sessionId = new SessionId($"{_conversationId}/{threadTs}");

        if (_dependencies.ThreadPropsFactory is not null)
            return _dependencies.ThreadPropsFactory(sessionId, channelId, threadTs, _dependencies);

        return SlackThreadBindingActor.CreateProps(
            sessionId: sessionId,
            channelId: channelId,
            threadTs: threadTs,
            dependencies: _dependencies);
    }
}
