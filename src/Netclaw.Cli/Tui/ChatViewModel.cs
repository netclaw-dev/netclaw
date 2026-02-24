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
    private IDisposable? _daemonOutputSubscription;
    private IDisposable? _daemonConnectionSubscription;

#pragma warning disable CS0169, CS0414 // Backing fields used by [Reactive] source generator
    [Reactive] private bool _isGenerating;
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

    private async Task InitializeSessionAsync()
    {
        try
        {
            _daemonOutputSubscription = _daemonClient.SessionOutput
                .Subscribe(output =>
                {
                    _outputSubject.OnNext(output);

                    // Track turn lifecycle for generation state
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
                    if (IsGenerating && evt.State is DaemonConnectionState.Connected)
                    {
                        StatusMessage = "Generating...";
                    }
                    else
                    {
                        StatusMessage = evt.Message;
                    }

                    RequestRedraw();
                });

            await ConnectWithRetryAsync();
            var sessionId = await _daemonClient.CreateSessionAsync("tui");
            SessionIdDisplay = sessionId;

            StatusMessage = "Ready";
            RequestRedraw();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
            RequestRedraw();
        }
    }

    /// <summary>
    /// Submit user text to the session pipeline.
    /// </summary>
    public async Task SubmitAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

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
            StatusMessage = $"Connection failed: {ex.Message}";
            RequestRedraw();
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

    private async Task ConnectWithRetryAsync()
    {
        var delays = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10)
        };

        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            try
            {
                await _daemonClient.ConnectAsync();
                return;
            }
            catch when (attempt < delays.Length)
            {
                StatusMessage = $"Connecting... retry {attempt + 1}/{delays.Length}";
                RequestRedraw();
                await Task.Delay(delays[attempt]);
            }
        }

        throw new InvalidOperationException("Unable to connect to daemon after retries.");
    }
}
