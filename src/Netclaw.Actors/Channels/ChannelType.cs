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
    Reminder,
    Webhook,
    Discord
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
        ChannelType.Webhook => "webhook",
        ChannelType.Discord => "discord",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static bool SupportsInteractiveApproval(this ChannelType value) => value switch
    {
        ChannelType.Slack => true,
        ChannelType.Discord => true,
        ChannelType.Tui => true,
        ChannelType.SignalR => true,
        _ => false
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
        if (string.Equals(wire, "webhook", StringComparison.OrdinalIgnoreCase))
        { value = ChannelType.Webhook; return true; }
        if (string.Equals(wire, "discord", StringComparison.OrdinalIgnoreCase))
        { value = ChannelType.Discord; return true; }
        value = default;
        return false;
    }
}
