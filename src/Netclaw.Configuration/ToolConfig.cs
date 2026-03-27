namespace Netclaw.Configuration;

/// <summary>
/// Shared configuration for first-party tool execution.
/// </summary>
public sealed class ToolConfig
{
    public ShellExecutionMode? ShellMode { get; set; }
    public int ShellTimeoutSeconds { get; set; } = 60;
    public int MaxOutputChars { get; set; } = 32_000;
    public ToolAudienceProfiles AudienceProfiles { get; set; } = new();
    public WebFetchConfig WebFetch { get; set; } = new();
}
