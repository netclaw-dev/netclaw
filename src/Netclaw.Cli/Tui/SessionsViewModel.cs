// -----------------------------------------------------------------------
// <copyright file="SessionsViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Reactive ViewModel for the session browser page (<c>netclaw sessions</c>).
/// Loads recent sessions from the daemon catalog and allows the user to
/// select one for resume in the chat page.
/// </summary>
public sealed class SessionsViewModel : ReactiveViewModel
{
    private const int PageSize = 50;

    private readonly DaemonApi _daemonApi;
    private readonly ChatNavigationState _navigationState;
    private readonly TimeProvider _timeProvider;
    private int _pageOffset;
    private bool _hasNextPage;

    public ReactiveProperty<string> StatusMessage { get; } = new("Loading sessions...");
    public ReactiveProperty<bool> IsLoading { get; } = new(true);
    public List<SessionCatalogEntryDto> Sessions { get; } = [];
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);

    public SessionsViewModel(
        DaemonApi daemonApi,
        ChatNavigationState navigationState,
        TimeProvider timeProvider)
    {
        _daemonApi = daemonApi;
        _navigationState = navigationState;
        _timeProvider = timeProvider;
    }

    public override void OnActivated()
    {
        base.OnActivated();

        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        _ = LoadSessionsAsync(offset: 0);
    }

    private async Task LoadSessionsAsync(int offset)
    {
        IsLoading.Value = true;
        RequestRedraw();

        try
        {
            var sessions = await _daemonApi.ListSessionsAsync(PageSize + 1, offset);
            _pageOffset = offset;
            _hasNextPage = sessions.Count > PageSize;

            Sessions.Clear();
            Sessions.AddRange(sessions.Take(PageSize));
            SelectedIndex.Value = 0;

            if (Sessions.Count == 0)
            {
                StatusMessage.Value = _pageOffset == 0
                    ? "No sessions found. Press Enter to start a new chat."
                    : "No older sessions found. [PgUp] Newer sessions  [N] New chat  [Ctrl+Q] Quit";
            }
            else
            {
                StatusMessage.Value = BuildStatusMessage();
            }
        }
        catch
        {
            _hasNextPage = false;
            StatusMessage.Value = "Failed to connect to daemon. Is it running?";
        }

        IsLoading.Value = false;
        RequestRedraw();
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        // Ctrl+Q always quits
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Shutdown();
            return;
        }

        // Escape quits
        if (keyInfo.Key == ConsoleKey.Escape)
        {
            Shutdown();
            return;
        }

        // N starts a new chat (no resume)
        if (keyInfo.Key == ConsoleKey.N && !keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            _navigationState.ResumeSessionId = null;
            Navigate?.Invoke("/chat");
            return;
        }

        if (IsLoading.Value || Sessions.Count == 0)
        {
            // Enter on empty state starts a new chat
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                _navigationState.ResumeSessionId = null;
                Navigate?.Invoke("/chat");
            }
            return;
        }

        switch (keyInfo.Key)
        {
            case ConsoleKey.PageUp:
                if (_pageOffset > 0)
                    _ = LoadSessionsAsync(Math.Max(0, _pageOffset - PageSize));
                break;

            case ConsoleKey.PageDown:
                if (_hasNextPage)
                    _ = LoadSessionsAsync(_pageOffset + PageSize);
                break;

            case ConsoleKey.UpArrow or ConsoleKey.K:
                if (SelectedIndex.Value > 0)
                {
                    SelectedIndex.Value--;
                    RequestRedraw();
                }
                break;

            case ConsoleKey.DownArrow or ConsoleKey.J:
                if (SelectedIndex.Value < Sessions.Count - 1)
                {
                    SelectedIndex.Value++;
                    RequestRedraw();
                }
                break;

            case ConsoleKey.Enter:
                var selected = Sessions[SelectedIndex.Value];
                _navigationState.ResumeSessionId = selected.SessionId;
                Navigate?.Invoke("/chat");
                break;
        }
    }

    private string BuildStatusMessage()
    {
        var start = _pageOffset + 1;
        var end = _pageOffset + Sessions.Count;
        var pagingHint = (_pageOffset > 0, _hasNextPage) switch
        {
            (true, true) => "  [PgUp] Newer  [PgDn] Older",
            (true, false) => "  [PgUp] Newer",
            (false, true) => "  [PgDn] Older",
            _ => ""
        };

        return $"Showing sessions {start}-{end}. [Enter] Resume  [N] New chat{pagingHint}  [Ctrl+Q] Quit";
    }

    /// <summary>
    /// Formats a Unix millisecond timestamp as a relative time string (e.g. "5m ago").
    /// </summary>
    public string FormatRelativeTime(long unixMs)
    {
        var now = _timeProvider.GetUtcNow();
        var then = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        var elapsed = now - then;

        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 30) return $"{(int)elapsed.TotalDays}d ago";
        return then.ToString("yyyy-MM-dd");
    }

    public override void Dispose()
    {
        StatusMessage.Dispose();
        IsLoading.Dispose();
        SelectedIndex.Dispose();
        base.Dispose();
    }
}
