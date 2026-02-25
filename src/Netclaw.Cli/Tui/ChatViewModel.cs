using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Reactive ViewModel for the chat page. Uses <see cref="DaemonClient"/>
/// to talk to the daemon-hosted session hub over SignalR.
/// </summary>
public partial class ChatViewModel : ReactiveViewModel
{
    private readonly DaemonClient _daemonClient;
    private readonly TimeProvider _timeProvider;
    private readonly SessionConfig _sessionConfig;

    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Queue<string> _pendingMessages = new();
    private IDisposable? _daemonOutputSubscription;
    private IDisposable? _daemonConnectionSubscription;
    private bool _sessionReady;
    private int _connectAttempts;

#pragma warning disable CS0169, CS0414 // Backing fields used by [Reactive] source generator
    [Reactive] private bool _isGenerating;
    [Reactive] private bool _isInputEnabled = true;
    [Reactive] private string _statusMessage = "Connecting...";
    [Reactive] private string? _sessionIdDisplay;
    [Reactive] private string? _usageDisplay;
#pragma warning restore CS0169, CS0414

    /// <summary>
    /// Observable stream of session output events. The page subscribes to this
    /// to render chat messages, tool activity, usage, etc.
    /// </summary>
    public IObservable<SessionOutput> SessionOutput => _outputSubject.AsObservable();

    /// <summary>
    /// The configured model identifier for display in the status bar.
    /// </summary>
    public string ModelId => _sessionConfig.ModelId;

    public int ContextWindowTokens => _sessionConfig.ContextWindowTokens;

    public ChatViewModel(
        DaemonClient daemonClient,
        TimeProvider timeProvider,
        SessionConfig sessionConfig)
    {
        _daemonClient = daemonClient;
        _timeProvider = timeProvider;
        _sessionConfig = sessionConfig;
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _ = InitializeSessionAsync();
    }

    private Task InitializeSessionAsync()
    {
        _daemonOutputSubscription = _daemonClient.SessionOutput
            .Subscribe(output =>
            {
                _outputSubject.OnNext(output);

                switch (output)
                {
                    case TurnCompleted:
                        IsGenerating = false;
                        break;
                    case ErrorOutput:
                        IsGenerating = false;
                        break;
                }

                RequestRedraw();
            });

        _daemonConnectionSubscription = _daemonClient.ConnectionEvents
            .Subscribe(evt =>
            {
                if (evt.State is DaemonConnectionState.Disconnected or DaemonConnectionState.Reconnecting)
                {
                    _sessionReady = false;
                }

                if (evt.State is DaemonConnectionState.Connected)
                {
                    _ = EnsureSessionAndFlushAsync();
                }

                if (IsGenerating && evt.State is DaemonConnectionState.Connected)
                    StatusMessage = "Generating...";
                else
                    StatusMessage = evt.Message;

                RequestRedraw();
            });

        _ = ConnectUntilReadyAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Submit user text to the session pipeline.
    /// </summary>
    public async Task SubmitAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!_sessionReady || !_daemonClient.IsConnected)
        {
            _pendingMessages.Enqueue(text);
            IsGenerating = false;
            IsInputEnabled = true;
            StatusMessage = $"Queued {_pendingMessages.Count} message(s). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
            return;
        }

        IsGenerating = true;
        StatusMessage = "Generating...";

        try
        {
            await _daemonClient.EnsureSessionAsync("tui");

            await _daemonClient.SendAsync(new ChannelInput
            {
                SenderId = "local-user",
                Contents = [new TextContent(text)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });
        }
        catch (Exception ex)
        {
            IsGenerating = false;
            _sessionReady = false;
            IsInputEnabled = true;
            _pendingMessages.Enqueue(text);
            StatusMessage = $"Send failed ({ex.Message}). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
        }
    }

    public void RequestAppShutdown()
    {
        Shutdown();
    }

    public override void Dispose()
    {
        _daemonOutputSubscription?.Dispose();
        _daemonConnectionSubscription?.Dispose();
        _outputSubject.Dispose();

        DisposeReactiveFields();
        base.Dispose();
    }

    private async Task ConnectUntilReadyAsync()
    {
        var delays = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10)
        };

        while (!_sessionReady)
        {
            try
            {
                await _daemonClient.ConnectAsync();
                await EnsureSessionAndFlushAsync();
                return;
            }
            catch
            {
                _connectAttempts++;
                var idx = Math.Min(_connectAttempts - 1, delays.Length - 1);
                StatusMessage = $"Connecting... retry {_connectAttempts} in {delays[idx].TotalSeconds:0}s";
                RequestRedraw();
                await Task.Delay(delays[idx]);
            }
        }
    }

    private async Task EnsureSessionAndFlushAsync()
    {
        var sessionId = await _daemonClient.EnsureSessionAsync("tui");
        SessionIdDisplay = sessionId;
        _sessionReady = true;
        IsInputEnabled = true;
        _connectAttempts = 0;

        while (_pendingMessages.Count > 0)
        {
            var pending = _pendingMessages.Dequeue();
            await _daemonClient.SendAsync(new ChannelInput
            {
                SenderId = "local-user",
                Contents = [new TextContent(pending)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });
        }

        if (!IsGenerating)
            StatusMessage = "Ready";

        RequestRedraw();
    }
}
