// -----------------------------------------------------------------------
// <copyright file="ChatNavigationState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui;

/// <summary>
/// Shared state for passing navigation parameters to <see cref="ChatViewModel"/>.
/// Registered as a singleton so that <c>SessionsViewModel</c> (or CLI arg parsing)
/// can set <see cref="ResumeSessionId"/> before navigating to the chat page.
/// </summary>
public sealed class ChatNavigationState
{
    /// <summary>
    /// Gets whether the session picker requested a separate inline chat host.
    /// </summary>
    public bool ChatLaunchRequested { get; private set; }

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

    /// <summary>
    /// Requests a new inline chat host after the full-screen picker exits.
    /// </summary>
    public void RequestChatLaunch(string? resumeSessionId)
    {
        ResumeSessionId = resumeSessionId;
        ChatLaunchRequested = true;
    }

    /// <summary>
    /// When set, <see cref="ChatViewModel"/> will auto-send this message
    /// (hidden from the UI) after the session is established. Used by the
    /// init wizard to trigger the onboarding interview.
    /// </summary>
    public string? InitialMessage { get; set; }

    /// <summary>
    /// Takes and clears the initial message in one operation.
    /// </summary>
    public string? TakeInitialMessage()
    {
        var msg = InitialMessage;
        InitialMessage = null;
        return msg;
    }
}
