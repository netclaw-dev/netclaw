namespace Netclaw.Actors.Tools;

/// <summary>
/// Shared configuration for first-party tool execution.
/// </summary>
public sealed class ToolConfig
{
    public int ShellTimeoutSeconds { get; init; } = 60;
    public int MaxOutputChars { get; init; } = 32_000;
}
