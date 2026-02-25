using Akka.Actor;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

public sealed class SlackConversationActor : ReceiveActor
{
    private readonly string _conversationId;
    private readonly SlackGatewayDependencies _dependencies;

    public SlackConversationActor(string conversationId, SlackGatewayDependencies dependencies)
    {
        _conversationId = conversationId;
        _dependencies = dependencies;

        Receive<SlackInboundMessage>(message =>
        {
            if (!IsAllowedConversation(message))
                return;

            if (!IsAllowedUser(message))
                return;

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
                return;

            if (decision is SlackRoutingDecision.ContinueOnly && !threadExists)
                return;

            var thread = existingThread.IsNobody()
                ? Context.ActorOf(CreateThreadProps(message.ChannelId, threadTs), threadActorName)
                : existingThread;

            var normalized = NormalizeInboundText(message.Text);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            thread.Forward(new SlackThreadInbound(
                SessionId: new SessionId($"{_conversationId}/{threadTs}"),
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
        if (!string.IsNullOrWhiteSpace(_dependencies.DefaultChannelId)
            && !string.Equals(message.ChannelId, _dependencies.DefaultChannelId, StringComparison.Ordinal))
            return false;

        if (_dependencies.Options.AllowedChannelIds is { Length: > 0 }
            && !_dependencies.Options.AllowedChannelIds.Contains(message.ChannelId, StringComparer.Ordinal))
            return false;

        return true;
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
