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
            var aclDecision = SlackAclPolicy.EvaluateInbound(
                message,
                _dependencies.Options,
                _dependencies.DefaultChannelId);

            if (!aclDecision.IsAllowed)
            {
                _log.Info("slack_event_dropped event={0} reason={1}", message.EventId, aclDecision.DenyReason ?? "acl_denied");
                ChannelTelemetry.RecordSlackEventDropped(aclDecision.DenyReason ?? "acl_denied");
                return;
            }

            if (IsBotMessage(message))
            {
                _log.Info("slack_event_filtered event={0} reason=bot_message", message.EventId);
                ChannelTelemetry.RecordSlackEventFiltered("bot_message");
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
                _dependencies.Options.MentionRequiredInDm,
                threadExists,
                containsMention);

            if (decision is SlackRoutingDecision.Ignore)
            {
                _log.Info("slack_event_filtered event={0} reason=routing_policy_ignore", message.EventId);
                ChannelTelemetry.RecordSlackEventFiltered("routing_policy_ignore");
                return;
            }

            if (decision is SlackRoutingDecision.ContinueOnly && !threadExists)
            {
                _log.Info("slack_event_dropped event={0} reason=thread_not_initialized", message.EventId);
                ChannelTelemetry.RecordSlackEventDropped("thread_not_initialized");
                return;
            }

            var thread = existingThread.IsNobody()
                ? Context.ActorOf(CreateThreadProps(message.ChannelId, threadTs), threadActorName)
                : existingThread;

            var normalized = NormalizeInboundText(message.Text);
            if (string.IsNullOrWhiteSpace(normalized) && message.Files is not { Count: > 0 })
            {
                _log.Info("slack_event_filtered event={0} reason=empty_text", message.EventId);
                ChannelTelemetry.RecordSlackEventFiltered("empty_text");
                return;
            }

            var sessionId = new SessionId($"{_conversationId.Value}/{threadTs.Value}");
            var turnId = string.IsNullOrWhiteSpace(message.EventId.Value)
                ? Guid.NewGuid().ToString("N")[..8]
                : message.EventId.Value;
            var log = _log
                .WithContext("SlackThreadTs", threadTs.Value)
                .WithContext("SessionId", sessionId.Value)
                .WithContext("TurnId", turnId)
                .WithContext("SlackEventId", message.EventId.Value);

            log.Info("slack_turn_routed event={EventId} hasFiles={HasFiles} textChars={TextLength}",
                message.EventId.Value,
                message.Files is { Count: > 0 },
                normalized.Length);

            thread.Forward(new SlackThreadInbound(
                SessionId: sessionId,
                ChannelId: message.ChannelId,
                ThreadTs: threadTs,
                EventId: message.EventId,
                TurnId: turnId,
                SenderId: message.UserId?.Value ?? "slack-user",
                Audience: aclDecision.Audience,
                Principal: aclDecision.Principal,
                Provenance: aclDecision.Provenance,
                Text: normalized,
                ReceivedAt: _dependencies.TimeProvider.GetUtcNow(),
                Files: message.Files));
        });

        Receive<StartProactiveThread>(message =>
        {
            // Defense-in-depth: validate channel ACL even though the tool already checked.
            // DM channels (D-prefixed) skip this — they were validated via user ACL + AllowDirectMessages.
            var isDmChannel = message.ChannelId.Value.StartsWith("D", StringComparison.Ordinal);
            if (isDmChannel && !_dependencies.Options.AllowDirectMessages)
            {
                var reason = "Direct messages are disabled by Slack channel configuration.";
                _log.Warning("Rejected proactive DM thread for channel {0}: {1}", message.ChannelId, reason);
                Sender.Tell(new Status.Failure(new InvalidOperationException(reason)));
                return;
            }

            if (!isDmChannel && !SlackAclPolicy.IsAllowedChannel(
                    message.ChannelId, _dependencies.Options, _dependencies.DefaultChannelId))
            {
                _log.Warning("Rejected proactive thread for disallowed channel {0}", message.ChannelId);
                Sender.Tell(new Status.Failure(new InvalidOperationException(
                    $"Channel {message.ChannelId.Value} is not in the allowed channels list.")));
                return;
            }

            var threadActorName = Uri.EscapeDataString(message.ThreadTs.Value);
            var existingThread = Context.Child(threadActorName);

            var thread = existingThread.IsNobody()
                ? Context.ActorOf(CreateThreadProps(message.ChannelId, message.ThreadTs), threadActorName)
                : existingThread;

            _log.Debug("Routing proactive thread setup to thread actor {0}", message.ThreadTs);
            thread.Forward(message);
        });
    }

    public static Props CreateProps(SlackChannelId conversationId, SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackConversationActor(conversationId, dependencies));

    private bool IsBotMessage(SlackInboundMessage message)
    {
        if (message.BotId is not null)
            return true;

        if (_dependencies.BotUserId is not null
            && message.UserId == _dependencies.BotUserId)
            return true;

        return false;
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
