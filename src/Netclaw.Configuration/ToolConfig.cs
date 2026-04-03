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

    /// <summary>
    /// Additional shell command patterns to add to the hard deny list.
    /// These are verb-chain prefixes that are categorically blocked
    /// and cannot be approved. Added to the compiled-in defaults.
    /// </summary>
    public List<string> HardDenyPatterns { get; set; } = [];
}
