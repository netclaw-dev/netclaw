namespace Netclaw.Configuration;

/// <summary>
/// Shared configuration for first-party tool execution.
/// </summary>
public sealed class ToolConfig
{
    public int ShellTimeoutSeconds { get; set; } = 60;
    public int MaxOutputChars { get; set; } = 32_000;
}
