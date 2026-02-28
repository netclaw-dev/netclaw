namespace Netclaw.Channels.Slack;

/// <summary>
/// Slack channel ID (e.g. <c>C...</c> for public, <c>D...</c> for DM).
/// </summary>
public readonly record struct SlackChannelId(string Value)
{
    public static explicit operator SlackChannelId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Slack thread timestamp — identifies the root message of a thread.
/// </summary>
public readonly record struct SlackThreadTs(string Value)
{
    /// <summary>
    /// Converts an event timestamp to a thread timestamp. Used when a message
    /// has no explicit <c>thread_ts</c> and becomes its own thread root.
    /// </summary>
    public static SlackThreadTs FromEventTs(SlackEventTs eventTs) => new(eventTs.Value);

    public static explicit operator SlackThreadTs(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Slack event timestamp — unique per event within a channel.
/// </summary>
public readonly record struct SlackEventTs(string Value)
{
    public static explicit operator SlackEventTs(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Deduplication key for Slack events, typically <c>{channelId}:{eventTs}</c>.
/// </summary>
public readonly record struct SlackEventId(string Value)
{
    public static explicit operator SlackEventId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Slack user ID (e.g. <c>U...</c>).
/// </summary>
public readonly record struct SlackUserId(string Value)
{
    public static explicit operator SlackUserId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Slack bot ID (e.g. <c>B...</c>).
/// </summary>
public readonly record struct SlackBotId(string Value)
{
    public static explicit operator SlackBotId(string value) => new(value);

    public override string ToString() => Value;
}
