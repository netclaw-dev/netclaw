namespace Netclaw.Tools;

/// <summary>
/// Strongly-typed tool call identity. Wraps the call ID string used
/// to correlate approval requests with approval decisions in the
/// interactive approval flow.
/// </summary>
public readonly record struct ToolCallId(string Value)
{
    public static explicit operator ToolCallId(string value) => new(value);

    public override string ToString() => Value;
}
