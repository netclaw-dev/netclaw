namespace Netclaw.Cli.Tui;

/// <summary>
/// Shared state for passing navigation parameters to <see cref="ChatViewModel"/>.
/// Registered as a singleton so that <c>SessionsViewModel</c> (or CLI arg parsing)
/// can set <see cref="ResumeSessionId"/> before navigating to the chat page.
/// </summary>
public sealed class ChatNavigationState
{
    /// <summary>
    /// When set, <see cref="ChatViewModel"/> will resume this session ID
    /// instead of creating a new one. Consumed (cleared) on first read.
    /// </summary>
    public string? ResumeSessionId { get; set; }

    /// <summary>
    /// Takes and clears the resume session ID in one operation.
    /// </summary>
    public string? TakeResumeSessionId()
    {
        var id = ResumeSessionId;
        ResumeSessionId = null;
        return id;
    }
}
