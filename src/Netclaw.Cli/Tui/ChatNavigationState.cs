// -----------------------------------------------------------------------
// <copyright file="ChatNavigationState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Shared state for passing navigation parameters to <see cref="ChatViewModel"/>.
/// Registered as a singleton so that <c>SessionsViewModel</c> (or CLI arg parsing)
/// can set <see cref="ResumeSessionId"/> before navigating to the chat page.
/// </summary>
public sealed class ChatNavigationState
{
    /// <summary>The final result survives page disposal for the terminal notice.</summary>
    public ChatCloseReceipt? CloseReceipt { get; internal set; }

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

    /// <summary>
    /// True once <see cref="StartOnboarding"/> queued the onboarding trigger. Stays set after
    /// the message is taken so a failed daemon connection can still point the operator at
    /// <c>netclaw chat --onboarding</c>.
    /// </summary>
    public bool IsOnboarding { get; private set; }

    /// <summary>
    /// Queues the onboarding trigger as the chat's first turn. The init wizard, the redo
    /// identity flow and <c>netclaw chat --onboarding</c> all start the interview through here.
    /// </summary>
    public void StartOnboarding(string trigger)
    {
        InitialMessage = trigger;
        IsOnboarding = true;
    }
}
