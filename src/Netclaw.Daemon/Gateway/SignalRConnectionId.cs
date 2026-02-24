namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Strongly-typed SignalR connection identity.
/// </summary>
public readonly record struct SignalRConnectionId(string Value)
{
    public static SignalRConnectionId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Connection ID cannot be empty.", nameof(value));

        return new SignalRConnectionId(value);
    }

    public override string ToString() => Value;
}
