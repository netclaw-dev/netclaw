namespace Netclaw.Actors.Channels;

/// <summary>
/// Identifies the transport channel through which a session communicates.
/// </summary>
public enum ChannelType
{
    Slack,
    Tui,
    Headless,
    SignalR,
    Reminder
}

public static class ChannelTypeExtensions
{
    public static string ToWireValue(this ChannelType value) => value switch
    {
        ChannelType.Slack => "slack",
        ChannelType.Tui => "tui",
        ChannelType.Headless => "headless",
        ChannelType.SignalR => "signalr",
        ChannelType.Reminder => "reminder",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool TryFromWireValue(string? wire, out ChannelType value)
    {
        if (string.Equals(wire, "slack", StringComparison.OrdinalIgnoreCase))
        { value = ChannelType.Slack; return true; }
        if (string.Equals(wire, "tui", StringComparison.OrdinalIgnoreCase))
        { value = ChannelType.Tui; return true; }
        if (string.Equals(wire, "headless", StringComparison.OrdinalIgnoreCase))
        { value = ChannelType.Headless; return true; }
        if (string.Equals(wire, "signalr", StringComparison.OrdinalIgnoreCase))
        { value = ChannelType.SignalR; return true; }
        if (string.Equals(wire, "reminder", StringComparison.OrdinalIgnoreCase))
        { value = ChannelType.Reminder; return true; }
        value = default;
        return false;
    }
}
