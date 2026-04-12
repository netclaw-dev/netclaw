using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using R3;
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
    private readonly ModelCapabilities _modelCapabilities;
    private string? _resumeSessionId;
    private string? _initialMessage;

    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Queue<string> _pendingMessages = new();
    private readonly Queue<ToolInteractionRequest> _pendingInteractions = new();
    private IDisposable? _daemonOutputSubscription;
    private IDisposable? _daemonConnectionSubscription;
    private bool _sessionReady;
    private int _connectAttempts;
    private readonly ObservableCollection<string> _approvalOptions = [];

    public ReactiveProperty<bool> IsGenerating { get; } = new(false);
    public ReactiveProperty<bool> IsInputEnabled { get; } = new(true);
    public ReactiveProperty<string> StatusMessage { get; } = new("Connecting...");
    public ReactiveProperty<string?> SessionIdDisplay { get; } = new(null);
    public ReactiveProperty<string?> UsageDisplay { get; } = new(null);
    public ReactiveProperty<int> UiVersion { get; } = new(0);

    /// <summary>
    /// Observable stream of session output events. The page subscribes to this
    /// to render chat messages, tool activity, usage, etc.
    /// </summary>
    public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();

    public bool HasPendingInteraction => _pendingInteractions.Count > 0;

    public IReadOnlyList<string> ApprovalOptions => _approvalOptions;

    public ToolInteractionRequest? CurrentInteraction => _pendingInteractions.Count > 0 ? _pendingInteractions.Peek() : null;

    /// <summary>
    /// The configured model identifier for display in the status bar.
    /// </summary>
    public string ModelId => _modelCapabilities.ModelId;

    public int ContextWindowTokens => _modelCapabilities.ContextWindowTokens;

    public ChatViewModel(
        DaemonClient daemonClient,
        TimeProvider timeProvider,
        ModelCapabilities modelCapabilities,
        ChatNavigationState navigationState)
    {
        _daemonClient = daemonClient;
        _timeProvider = timeProvider;
        _modelCapabilities = modelCapabilities;
        _resumeSessionId = navigationState.TakeResumeSessionId();
        _initialMessage = navigationState.TakeInitialMessage();
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
                    case ToolInteractionRequest interaction:
                        _pendingInteractions.Enqueue(interaction);
                        RefreshApprovalOptions();
                        IsGenerating.Value = false;
                        StatusMessage.Value = "Approval required";
                        break;
                    case TurnCompleted:
                        _pendingInteractions.Clear();
                        RefreshApprovalOptions();
                        IsGenerating.Value = false;
                        break;
                    case ErrorOutput:
                        _pendingInteractions.Clear();
                        RefreshApprovalOptions();
                        IsGenerating.Value = false;
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
                    IsGenerating.Value = false;
                }

                if (evt.State is DaemonConnectionState.Connected)
                {
                    _ = EnsureSessionAndFlushAsync();
                }

                if (IsGenerating.Value && evt.State is DaemonConnectionState.Connected)
                    StatusMessage.Value = "Generating...";
                else
                    StatusMessage.Value = evt.Message;

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

        if (_pendingInteractions.Count > 0)
        {
            await SubmitInteractionResponseAsync(text);
            return;
        }

        if (!_sessionReady || !_daemonClient.IsConnected)
        {
            _pendingMessages.Enqueue(text);
            IsGenerating.Value = false;
            IsInputEnabled.Value = true;
            StatusMessage.Value = $"Queued {_pendingMessages.Count} message(s). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
            return;
        }

        IsGenerating.Value = true;
        StatusMessage.Value = "Generating...";

        try
        {
            await _daemonClient.EnsureSessionAsync(DaemonClient.TuiChannelType);

            await _daemonClient.SendAsync(new ChannelInput
            {
                SenderId = "local-user",
                Contents = [new TextContent(text)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });
        }
        catch (Exception ex)
        {
            IsGenerating.Value = false;
            _sessionReady = false;
            IsInputEnabled.Value = true;
            _pendingMessages.Enqueue(text);
            StatusMessage.Value = $"Send failed ({ex.Message}). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
        }
    }

    public void RequestAppShutdown()
    {
        Shutdown();
    }

    private async Task SubmitInteractionResponseAsync(string text)
    {
        if (!_sessionReady || !_daemonClient.IsConnected)
        {
            StatusMessage.Value = "Approval required. Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
            return;
        }

        if (!ToolInteractionResponseParser.TryParseApprovalResponse(text, out var selectedKey) || selectedKey is null)
        {
            StatusMessage.Value = "Approval required: reply A, B, C, or D.";
            RequestRedraw();
            return;
        }

        var pending = _pendingInteractions.Peek();

        try
        {
            await _daemonClient.EnsureSessionAsync(DaemonClient.TuiChannelType);
            await _daemonClient.RespondToInteractionAsync(pending.CallId, selectedKey);

            _pendingInteractions.Dequeue();
            RefreshApprovalOptions();
            IsGenerating.Value = _pendingInteractions.Count == 0;
            StatusMessage.Value = _pendingInteractions.Count == 0
                ? "Generating..."
                : "Approval required";
            RequestRedraw();
        }
        catch (Exception ex)
        {
            _sessionReady = false;
            IsGenerating.Value = false;
            StatusMessage.Value = $"Approval response failed ({ex.Message}). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
        }
    }

    public Task SubmitInteractionOptionAsync(string optionLabel)
    {
        if (CurrentInteraction is null)
            return Task.CompletedTask;

        var option = CurrentInteraction.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, optionLabel, StringComparison.Ordinal));
        if (option is null)
            return Task.CompletedTask;

        return SubmitInteractionSelectionAsync(option.Key);
    }

    public string GetApprovalPrompt()
    {
        if (CurrentInteraction is not { } interaction)
            return "Approval required";

        var patterns = interaction.Patterns.Count > 0
            ? $" Patterns: {string.Join(", ", interaction.Patterns)}"
            : string.Empty;
        return $"Approval required for {interaction.ToolName}. {interaction.DisplayText}{patterns}";
    }

    public string? GetApprovalHint()
    {
        if (!HasPendingInteraction)
            return null;

        return "Select an option and press Enter.";
    }

    public override void Dispose()
    {
        _daemonOutputSubscription?.Dispose();
        _daemonConnectionSubscription?.Dispose();
        _outputSubject.Dispose();

        IsGenerating.Dispose();
        IsInputEnabled.Dispose();
        StatusMessage.Dispose();
        SessionIdDisplay.Dispose();
        UsageDisplay.Dispose();
        UiVersion.Dispose();
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
                StatusMessage.Value = $"Connecting... retry {_connectAttempts} in {delays[idx].TotalSeconds:0}s";
                RequestRedraw();
                await Task.Delay(delays[idx]);
            }
        }
    }

    private async Task EnsureSessionAndFlushAsync()
    {
        // On the first call, use ResumeSessionAsync if a resume ID was provided.
        // After that, DaemonClient has the session ID cached, so use EnsureSessionAsync
        // to avoid redundant resume calls on reconnect.
        var resumeId = _resumeSessionId;
        _resumeSessionId = null;
        var sessionId = resumeId is not null
            ? await _daemonClient.ResumeSessionAsync(resumeId, DaemonClient.TuiChannelType)
            : await _daemonClient.EnsureSessionAsync(DaemonClient.TuiChannelType);
        SessionIdDisplay.Value = sessionId;
        _sessionReady = true;
        IsInputEnabled.Value = true;
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

        // Auto-send hidden trigger message (e.g., onboarding interview prompt).
        // Not rendered as a user bubble — the LLM's greeting is the first visible thing.
        if (_initialMessage is not null)
        {
            var trigger = _initialMessage;
            _initialMessage = null;
            IsGenerating.Value = true;
            StatusMessage.Value = "Generating...";
            RequestRedraw();
            await _daemonClient.SendAsync(new ChannelInput
            {
                SenderId = "system-init",
                Contents = [new TextContent(trigger)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });
            return;
        }

        if (!IsGenerating.Value)
            StatusMessage.Value = "Ready";

        RequestRedraw();
    }

    private async Task SubmitInteractionSelectionAsync(string selectedKey)
    {
        if (CurrentInteraction is null)
            return;

        if (!_sessionReady || !_daemonClient.IsConnected)
        {
            StatusMessage.Value = "Approval required. Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
            return;
        }

        var pending = _pendingInteractions.Peek();

        try
        {
            await _daemonClient.EnsureSessionAsync(DaemonClient.TuiChannelType);
            await _daemonClient.RespondToInteractionAsync(pending.CallId, selectedKey);

            _pendingInteractions.Dequeue();
            RefreshApprovalOptions();
            IsGenerating.Value = _pendingInteractions.Count == 0;
            StatusMessage.Value = _pendingInteractions.Count == 0
                ? "Generating..."
                : "Approval required";
            RequestRedraw();
        }
        catch (Exception ex)
        {
            _sessionReady = false;
            IsGenerating.Value = false;
            StatusMessage.Value = $"Approval response failed ({ex.Message}). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
        }
    }

    private void RefreshApprovalOptions()
    {
        _approvalOptions.Clear();
        if (CurrentInteraction is { } interaction)
        {
            foreach (var option in interaction.Options)
                _approvalOptions.Add(option.Label);
        }

        UiVersion.Value++;
    }
}
