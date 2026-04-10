using System.Globalization;

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
/// Slack event timestamp — unique per event within a channel. Wire format is
/// Unix seconds with microsecond precision, e.g. <c>1712700000.000500</c>.
/// </summary>
public readonly record struct SlackEventTs(string Value) : IComparable<SlackEventTs>
{
    public static explicit operator SlackEventTs(string value) => new(value);

    public override string ToString() => Value;

    /// <summary>
    /// Parses the wire-format ts as a decimal for strict monotonic comparison.
    /// Returns <c>false</c> if the value is null, empty, or not a valid number.
    /// </summary>
    public bool TryToDecimal(out decimal value)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            value = default;
            return false;
        }

        return decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Converts the ts to a <see cref="DateTimeOffset"/> using Slack's
    /// Unix-seconds encoding. Returns <c>null</c> if the value cannot be parsed.
    /// </summary>
    public DateTimeOffset? ToDateTimeOffset()
    {
        if (!TryToDecimal(out var seconds))
            return null;

        var milliseconds = (long)(seconds * 1000m);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    /// <summary>
    /// Returns the sign of the comparison between this ts and <paramref name="other"/>.
    /// An unparseable operand is treated as equal to another unparseable operand and
    /// less than any parseable one, but callers should validate ts values up-front.
    /// </summary>
    public int CompareTo(SlackEventTs other)
    {
        var leftOk = TryToDecimal(out var l);
        var rightOk = other.TryToDecimal(out var r);
        if (leftOk && rightOk) return l.CompareTo(r);
        if (leftOk) return 1;
        if (rightOk) return -1;
        return 0;
    }
}

/// <summary>
/// Deduplication key for Slack events, typically <c>{channelId}:{eventTs}</c>.
/// </summary>
public readonly record struct SlackEventId(string Value)
{
    public static explicit operator SlackEventId(string value) => new(value);

    public override string ToString() => Value;

    /// <summary>
    /// Extracts the <see cref="SlackEventTs"/> portion of an event id in the
    /// form <c>{channelId}:{ts}</c>. Returns <c>null</c> if the value is empty,
    /// lacks a separator, or the ts portion is not a valid Slack ts.
    /// </summary>
    public SlackEventTs? TryGetEventTs()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return null;

        var separator = Value.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0 || separator == Value.Length - 1)
            return null;

        var candidate = new SlackEventTs(Value[(separator + 1)..]);
        return candidate.TryToDecimal(out _) ? candidate : null;
    }
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
