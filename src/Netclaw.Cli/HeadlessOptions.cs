namespace Netclaw.Cli;

/// <summary>
/// Configuration for headless (<c>chat -p</c>) mode.
/// </summary>
public sealed record HeadlessOptions
{
    public required string Prompt { get; init; }
    public string? ResumeSessionId { get; init; }
    public bool JsonOutput { get; init; }
}
