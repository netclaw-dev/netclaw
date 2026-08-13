// -----------------------------------------------------------------------
// <copyright file="ChatViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tools;
using R3;
using Termina.Reactive;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Reactive ViewModel for the chat page. Uses <see cref="DaemonClient"/>
/// to talk to the daemon-hosted session hub over SignalR.
/// </summary>
public partial class ChatViewModel : ReactiveViewModel
{
    /// <summary>
    /// Cap on the approval body shown in the expanded (Ctrl+O) view.
    /// Without it, a multi-KB command handed verbatim to a TextNode can
    /// break the terminal renderer.
    /// </summary>
    internal const int MaxExpandedApprovalBodyChars = 8000;

    private readonly DaemonClient _daemonClient;
    private readonly TimeProvider _timeProvider;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly NetclawPaths _paths;
    private string? _resumeSessionId;
    private string? _initialMessage;

    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Queue<PendingUserMessage> _pendingMessages = new();
    private readonly object _pendingMessagesGate = new();
    private readonly SemaphoreSlim _sessionAttachGate = new(1, 1);
    private readonly Queue<ToolInteractionRequest> _pendingInteractions = new();
    private readonly object _queuedTurnMessagesGate = new();
    private readonly HashSet<string> _queuedTurnMessageIds = new(StringComparer.Ordinal);
    private int _legacyQueuedTurnMessageCount;

    /// <summary>
    /// True while an interaction response is in flight to the daemon. Guards
    /// against duplicate submissions (e.g. Escape + Enter pressed back to
    /// back) so only one <see cref="ToolInteractionResponse"/> is sent per
    /// interaction. Released on success (interaction dequeued) and on failure
    /// (prompt re-presented for retry) alike.
    /// </summary>
    private bool _isSubmittingInteraction;
    private string? _submittedInteractionCallId;
    private IDisposable? _daemonOutputSubscription;
    private IDisposable? _daemonConnectionSubscription;
    // Per-session USAGE log writer. Mirrors HeadlessChannel's writer so the
    // canonical signalr-{sessionId}.log file receives USAGE: in=... cached=...
    // lines for both TUI and -p driven sessions. Without this, post-hoc KV
    // cache analysis and eval tooling that anchors on the per-session log
    // silently gets no data from TUI turns (issue #1173).
    private StreamWriter? _usageLog;
    private volatile bool _sessionReady;
    private int _connectAttempts;
    private readonly ObservableCollection<string> _approvalOptions = [];

    public ReactiveProperty<bool> IsGenerating { get; } = new(false);
    public ReactiveProperty<bool> IsInputEnabled { get; } = new(true);
    public ReactiveProperty<string> StatusMessage { get; } = new("Connecting...");
    public ReactiveProperty<string?> SessionIdDisplay { get; } = new(null);
    public ReactiveProperty<string?> UsageDisplay { get; } = new(null);
    public ReactiveProperty<int> UiVersion { get; } = new(0);
    internal ReactiveProperty<int> QueuedTurnMessageCount { get; } = new(0);

    /// <summary>
    /// When true, the approval prompt body renders in full inside the Input
    /// panel. When false (default), the body is truncated to a single line so
    /// the selection list and status controls remain visible (issue #1132).
    /// Toggled by the page via <see cref="ToggleApprovalDetail"/>.
    /// </summary>
    public ReactiveProperty<bool> IsApprovalDetailVisible { get; } = new(false);

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
        ChatNavigationState navigationState,
        NetclawPaths paths)
    {
        _daemonClient = daemonClient;
        _timeProvider = timeProvider;
        _modelCapabilities = modelCapabilities;
        _paths = paths;
        _resumeSessionId = navigationState.TakeResumeSessionId();
        _initialMessage = navigationState.TakeInitialMessage();
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _ = InitializeSessionAsync();
    }

    protected virtual Task InitializeSessionAsync()
    {
        _daemonOutputSubscription = _daemonClient.SessionOutput
            .Subscribe(output =>
            {
                _outputSubject.OnNext(output);

                if (output is UsageOutput usage)
                {
                    AppendUsageLog(usage);
                }

                switch (output)
                {
                    case ToolInteractionRequest interaction:
                        EnqueuePendingInteraction(interaction);
                        RefreshApprovalOptions();
                        IsGenerating.Value = false;
                        StatusMessage.Value = "Approval required";
                        break;
                    case ApprovalOutcomeOutput outcome:
                        RemovePendingInteraction(outcome.CallId.Value);
                        if (string.Equals(
                                _submittedInteractionCallId,
                                outcome.CallId.Value,
                                StringComparison.Ordinal))
                        {
                            _submittedInteractionCallId = null;
                            _isSubmittingInteraction = false;
                        }
                        RefreshApprovalOptions();
                        IsGenerating.Value = _pendingInteractions.Count == 0;
                        StatusMessage.Value = _pendingInteractions.Count == 0
                            ? "Generating..."
                            : "Approval required";
                        break;
                    case TurnCompleted:
                        _pendingInteractions.Clear();
                        _submittedInteractionCallId = null;
                        _isSubmittingInteraction = false;
                        RefreshApprovalOptions();
                        var queuedCount = CompleteLegacyQueuedTurnMessages();
                        IsGenerating.Value = queuedCount > 0;
                        StatusMessage.Value = queuedCount > 0
                            ? "Generating..."
                            : "Ready";
                        break;
                    case UserMessagesPulledOutput pulled:
                        RecordPulledTurnMessages(pulled.Messages);
                        IsGenerating.Value = true;
                        StatusMessage.Value = "Generating...";
                        break;
                    case ErrorOutput:
                        _pendingInteractions.Clear();
                        _submittedInteractionCallId = null;
                        _isSubmittingInteraction = false;
                        RefreshApprovalOptions();
                        IsGenerating.Value = GetQueuedTurnMessageCount() > 0;
                        break;
                }

                RequestRedraw();
            });

        _daemonConnectionSubscription = _daemonClient.ConnectionEvents
            .Subscribe(evt =>
            {
                if (evt.State is DaemonConnectionState.Disconnected
                    or DaemonConnectionState.Reconnecting
                    or DaemonConnectionState.TransportClosed)
                {
                    SetSessionReady(false);
                }

                // The initial connect path owns the first session attach.
                // A later Connected event restores an existing attached session.
                if (evt.State is DaemonConnectionState.Connected
                    && SessionIdDisplay.Value is not null
                    && !IsSessionReady())
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
    public virtual Task SubmitAsync(string text) => SubmitCoreAsync(text, null);

    public virtual Task SubmitAsync(string text, string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return SubmitCoreAsync(text, messageId);
    }

    internal static string CreateUserMessageId() => $"tui:{Guid.NewGuid():N}";

    private async Task SubmitCoreAsync(string text, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (_pendingInteractions.Count > 0)
        {
            await SubmitInteractionResponseAsync(text);
            return;
        }

        var isActiveTurnPrompt = IsGenerating.Value;
        if (isActiveTurnPrompt)
            AddQueuedTurnMessage(messageId);

        var pendingMessage = new PendingUserMessage(text, messageId);
        if (TryEnqueuePendingMessageWhenUnavailable(pendingMessage, out var pendingCount))
        {
            IsGenerating.Value = isActiveTurnPrompt;
            IsInputEnabled.Value = true;
            StatusMessage.Value = $"Queued {pendingCount} message(s). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
            return;
        }

        if (!isActiveTurnPrompt)
        {
            IsGenerating.Value = true;
            StatusMessage.Value = "Generating...";
        }

        try
        {
            await SendUserMessageAsync(pendingMessage);
        }
        catch (Exception ex)
        {
            IsInputEnabled.Value = true;
            SetNotReadyAndEnqueuePendingMessage(pendingMessage);
            StatusMessage.Value = $"Send failed ({ex.Message}). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
        }
    }

    public virtual void RequestAppShutdown()
    {
        Shutdown();
    }

    private void AddQueuedTurnMessage(string? messageId)
    {
        int count;
        lock (_queuedTurnMessagesGate)
        {
            if (messageId is null)
                _legacyQueuedTurnMessageCount++;
            else if (!_queuedTurnMessageIds.Add(messageId))
                throw new InvalidOperationException($"The queued message ID '{messageId}' is not unique.");

            count = _legacyQueuedTurnMessageCount + _queuedTurnMessageIds.Count;
        }

        QueuedTurnMessageCount.Value = count;
        RequestRedraw();
    }

    private void RecordPulledTurnMessages(IReadOnlyList<PulledUserMessage> pulledMessages)
    {
        int remaining;
        lock (_queuedTurnMessagesGate)
        {
            foreach (var pulled in pulledMessages)
                _queuedTurnMessageIds.Remove(pulled.MessageId);
            remaining = _legacyQueuedTurnMessageCount + _queuedTurnMessageIds.Count;
        }

        QueuedTurnMessageCount.Value = remaining;
    }

    private int CompleteLegacyQueuedTurnMessages()
    {
        int remaining;
        lock (_queuedTurnMessagesGate)
        {
            _legacyQueuedTurnMessageCount = 0;
            remaining = _queuedTurnMessageIds.Count;
        }

        QueuedTurnMessageCount.Value = remaining;
        return remaining;
    }

    private int GetQueuedTurnMessageCount()
    {
        lock (_queuedTurnMessagesGate)
            return _legacyQueuedTurnMessageCount + _queuedTurnMessageIds.Count;
    }

    private async Task SubmitInteractionResponseAsync(string text)
    {
        if (!IsSessionReady() || !_daemonClient.IsConnected)
        {
            StatusMessage.Value = "Approval required. Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
            return;
        }

        var interaction = CurrentInteraction;
        if (interaction is null)
            return;

        if (!ToolInteractionResponseParser.TryParseApprovalResponse(text, interaction.Options, out var selectedKey) || selectedKey is null)
        {
            StatusMessage.Value = $"Approval required: reply with {FormatReplyLetters(interaction.Options)}.";
            RequestRedraw();
            return;
        }

        await SubmitInteractionSelectionAsync(selectedKey);
    }

    public Task SubmitInteractionOptionAsync(string optionLabel)
    {
        if (CurrentInteraction is not { } interaction)
            return Task.CompletedTask;

        return SubmitInteractionOptionAsync(interaction.CallId, optionLabel);
    }

    internal Task SubmitInteractionOptionAsync(ToolCallId expectedCallId, string optionLabel)
    {
        if (CurrentInteraction is not { } interaction
            || !string.Equals(
                interaction.CallId.Value,
                expectedCallId.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var option = interaction.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, optionLabel, StringComparison.Ordinal));
        if (option is null)
            return Task.CompletedTask;

        return SubmitInteractionSelectionAsync(option.Key.Value);
    }

    /// <summary>
    /// Denies the current pending approval interaction, if one exists.
    /// Maps to the first-class <see cref="ApprovalOptionKeys.Deny"/> wire key,
    /// so the session records a hard refusal of this call only (no ban on the
    /// verb for future invocations). Called by the TUI when the user presses
    /// Escape while an approval prompt is up — Escape means "cancel the
    /// dialog", not "quit the app" (#1757).
    /// </summary>
    internal virtual Task DenyPendingInteractionAsync()
    {
        if (CurrentInteraction is not { } interaction)
            return Task.CompletedTask;

        return DenyPendingInteractionAsync(interaction.CallId);
    }

    internal Task DenyPendingInteractionAsync(ToolCallId expectedCallId)
    {
        if (CurrentInteraction is not { } interaction
            || !string.Equals(
                interaction.CallId.Value,
                expectedCallId.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var denyOption = interaction.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Key.Value, ApprovalOptionKeys.Deny, StringComparison.Ordinal));
        if (denyOption is null)
            return Task.CompletedTask;

        return SubmitInteractionSelectionAsync(denyOption.Key.Value);
    }

    /// <summary>
    /// Single-line headline of the current approval interaction, e.g.
    /// <c>"Approval required for shell_execute."</c>. Always fits in the
    /// Input panel regardless of how long the underlying command is.
    /// </summary>
    public string GetApprovalSummary()
    {
        if (CurrentInteraction is not { } interaction)
            return "Approval required";

        return interaction.ToolName.IsMcp
            ? $"MCP tool approval required: {interaction.ToolName}."
            : $"Approval required for {interaction.ToolName}.";
    }

    /// <summary>
    /// Body of the current approval interaction (the command/path being
    /// approved, plus any explicit patterns). When
    /// <see cref="IsApprovalDetailVisible"/> is <c>false</c>, the body is
    /// returned as a single line truncated to <paramref name="maxLineWidth"/>
    /// with an ellipsis and a Ctrl+O hint. When <c>true</c>, the full
    /// untruncated body is returned and the caller is expected to wrap it
    /// inside a height-bounded layout node.
    /// </summary>
    public string GetApprovalBody(int maxLineWidth)
    {
        if (CurrentInteraction is not { } interaction)
            return string.Empty;

        var patterns = !interaction.ToolName.IsMcp && interaction.Patterns.Count > 0
            ? $" Patterns: {string.Join(", ", interaction.Patterns)}"
            : string.Empty;

        var prefix = interaction.ToolName.IsMcp ? "Invocation: " : string.Empty;
        var fullBody = $"{prefix}{interaction.DisplayText}{patterns}";

        if (IsApprovalDetailVisible.Value)
            return Netclaw.Channels.ApprovalDisplayTextFormatter.Truncate(fullBody, MaxExpandedApprovalBodyChars);

        // Collapse to one line: any embedded newlines would also push the
        // selection list past the panel cap, not just length.
        var singleLine = fullBody.ReplaceLineEndings(" ");

        const string hint = " [Ctrl+O to view full]";
        var budget = Math.Max(8, maxLineWidth - hint.Length);
        if (singleLine.Length <= budget)
            return singleLine + (singleLine.Length == fullBody.Length ? string.Empty : hint);

        return string.Concat(singleLine.AsSpan(0, budget - 1), "…", hint);
    }

    public string? GetApprovalHint()
    {
        if (!HasPendingInteraction)
            return null;

        return "Select an option and press Enter.";
    }

    /// <summary>
    /// Flips <see cref="IsApprovalDetailVisible"/> and forces a re-render.
    /// Invoked by <c>ChatPage</c> on Ctrl+O when an approval is pending.
    /// </summary>
    public void ToggleApprovalDetail()
    {
        if (!HasPendingInteraction)
            return;

        IsApprovalDetailVisible.Value = !IsApprovalDetailVisible.Value;
        UiVersion.Value++;
    }

    /// <summary>
    /// Test seam: stage a pending approval interaction as if the daemon had
    /// emitted it. Mirrors the production handler in
    /// <see cref="InitializeSessionAsync"/>. Used by <c>ChatPageTests</c> to
    /// exercise the Input-panel rendering without spinning up a daemon.
    /// </summary>
    internal void SeedPendingInteractionForTesting(ToolInteractionRequest interaction)
    {
        _outputSubject.OnNext(interaction);
        EnqueuePendingInteraction(interaction);
        RefreshApprovalOptions();
        IsGenerating.Value = false;
        StatusMessage.Value = "Approval required";
        RequestRedraw();
    }

    /// <summary>
    /// Test seam that publishes a session output without a daemon connection.
    /// </summary>
    internal void PublishOutputForTesting(SessionOutput output)
    {
        _outputSubject.OnNext(output);
    }

    /// <summary>
    /// Opens the per-session USAGE log file if not already open. Matches
    /// HeadlessChannel's filename and append semantics so a single session
    /// driven by both -p (headless) and TUI clients accumulates USAGE
    /// lines in one canonical log. Safe to call repeatedly — guarded by
    /// the null check so reconnects don't reopen the file.
    /// </summary>
    private void OpenUsageLogIfNeeded(string sessionIdValue)
    {
        if (_usageLog is not null)
            return;

        try
        {
            var logFileName = $"signalr-{sessionIdValue.Replace("/", "-", StringComparison.Ordinal)}.log";
            var logPath = Path.Combine(_paths.LogsDirectory, logFileName);
            _usageLog = new StreamWriter(logPath, append: true) { AutoFlush = true };
            _usageLog.WriteLine($"[{_timeProvider.GetUtcNow():o}] TUI session attached: {sessionIdValue}");
        }
        catch (IOException ex)
        {
            // Logging is best-effort — never let a log-file failure break
            // the live chat session. The daemon-side SessionLogActor at
            // ~/.netclaw/logs/sessions/{id}/session.log remains the
            // authoritative audit trail; surface the open failure to
            // Debug so a misconfigured logs directory is at least visible
            // under a debugger rather than silently lost.
            System.Diagnostics.Debug.WriteLine($"ChatViewModel: failed to open USAGE log for session {sessionIdValue}: {ex.Message}");
            _usageLog = null;
        }
    }

    /// <summary>
    /// Writes a USAGE line in the exact format produced by
    /// HeadlessChannel.cs so existing tooling that grep's for "USAGE: in="
    /// in the per-session log Just Works against TUI sessions too.
    /// </summary>
    private void AppendUsageLog(UsageOutput msg)
    {
        if (_usageLog is null)
            return;

        try
        {
            _usageLog.WriteLine(
                $"[{_timeProvider.GetUtcNow():o}] USAGE: in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens} cached={msg.CachedInputTokens} reasoning={msg.ReasoningTokens} context_window={msg.ContextWindowTokens} prompt_ms={msg.PromptMs} predicted_tok_s={msg.PredictedPerSecond}");
        }
        catch (IOException ex)
        {
            // See OpenUsageLogIfNeeded: best-effort logging that must not
            // affect the live session. Disk-full or rotation races land
            // here and would otherwise spam every turn — disable further
            // attempts on this session by dropping the writer reference.
            System.Diagnostics.Debug.WriteLine($"ChatViewModel: USAGE log write failed, disabling per-session log: {ex.Message}");
            _usageLog.Dispose();
            _usageLog = null;
        }
    }

    public override void Dispose()
    {
        _daemonOutputSubscription?.Dispose();
        _daemonConnectionSubscription?.Dispose();
        _outputSubject.Dispose();
        _usageLog?.Dispose();
        _usageLog = null;

        IsGenerating.Dispose();
        IsInputEnabled.Dispose();
        StatusMessage.Dispose();
        SessionIdDisplay.Dispose();
        UsageDisplay.Dispose();
        UiVersion.Dispose();
        QueuedTurnMessageCount.Dispose();
        IsApprovalDetailVisible.Dispose();
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

        while (!IsSessionReady())
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
        await _sessionAttachGate.WaitAsync();
        try
        {
            if (IsSessionReady() && _daemonClient.IsConnected)
                return;

            // Keep the resume ID until the attach succeeds. A transient attach
            // failure must not create a replacement session on the next attempt.
            var resumeId = _resumeSessionId;
            var sessionId = resumeId is not null
                ? await _daemonClient.ResumeSessionAsync(resumeId, DaemonClient.TuiChannelType)
                : await _daemonClient.EnsureSessionAsync(DaemonClient.TuiChannelType);
            if (resumeId is not null)
                _resumeSessionId = null;

            SessionIdDisplay.Value = sessionId;
            OpenUsageLogIfNeeded(sessionId);
            IsInputEnabled.Value = true;
            _connectAttempts = 0;

            while (true)
            {
                PendingUserMessage? pending;
                lock (_pendingMessagesGate)
                {
                    pending = _pendingMessages.Count == 0
                        ? null
                        : _pendingMessages.Peek();
                }

                if (pending is not null)
                {
                    await SendUserMessageAsync(pending);
                    lock (_pendingMessagesGate)
                    {
                        if (_pendingMessages.Count == 0
                            || _pendingMessages.Peek() != pending)
                        {
                            throw new InvalidOperationException("The pending message queue changed during its ordered flush.");
                        }

                        _pendingMessages.Dequeue();
                    }

                    continue;
                }

                // Auto-send a hidden trigger before this client becomes ready.
                // Clear it only after the daemon accepts it.
                if (_initialMessage is not null)
                {
                    var trigger = _initialMessage;
                    IsGenerating.Value = true;
                    StatusMessage.Value = "Generating...";
                    RequestRedraw();
                    await _daemonClient.SendAsync(trigger);
                    _initialMessage = null;
                    continue;
                }

                lock (_pendingMessagesGate)
                {
                    if (_pendingMessages.Count > 0)
                        continue;

                    _sessionReady = true;
                    break;
                }
            }

            if (!IsGenerating.Value)
                StatusMessage.Value = "Ready";

            RequestRedraw();
        }
        catch
        {
            SetSessionReady(false);
            throw;
        }
        finally
        {
            _sessionAttachGate.Release();
        }
    }

    private bool IsSessionReady()
    {
        lock (_pendingMessagesGate)
            return _sessionReady;
    }

    private void SetSessionReady(bool value)
    {
        lock (_pendingMessagesGate)
            _sessionReady = value;
    }

    private bool TryEnqueuePendingMessageWhenUnavailable(PendingUserMessage message, out int pendingCount)
    {
        lock (_pendingMessagesGate)
        {
            if (_sessionReady && _daemonClient.IsConnected)
            {
                pendingCount = _pendingMessages.Count;
                return false;
            }

            _pendingMessages.Enqueue(message);
            pendingCount = _pendingMessages.Count;
            return true;
        }
    }

    private void SetNotReadyAndEnqueuePendingMessage(PendingUserMessage message)
    {
        lock (_pendingMessagesGate)
        {
            _sessionReady = false;
            _pendingMessages.Enqueue(message);
        }
    }

    private Task SendUserMessageAsync(PendingUserMessage message) => message.MessageId is null
        ? _daemonClient.SendAsync(message.Text)
        : _daemonClient.SendWithIdAsync(message.Text, message.MessageId);

    private sealed record PendingUserMessage(string Text, string? MessageId);

    protected virtual async Task SubmitInteractionSelectionAsync(string selectedKey)
    {
        if (CurrentInteraction is null)
            return;

        if (_isSubmittingInteraction)
            return;

        if (!IsSessionReady() || !_daemonClient.IsConnected)
        {
            StatusMessage.Value = "Approval required. Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
            return;
        }

        var pending = _pendingInteractions.Peek();

        try
        {
            _isSubmittingInteraction = true;
            _submittedInteractionCallId = pending.CallId.Value;
            await _daemonClient.EnsureSessionAsync(DaemonClient.TuiChannelType);
            await _daemonClient.RespondToInteractionAsync(pending.CallId.Value, selectedKey);

            if (string.Equals(
                    _submittedInteractionCallId,
                    pending.CallId.Value,
                    StringComparison.Ordinal))
            {
                StatusMessage.Value = "Submitting decision...";
                RequestRedraw();
            }
        }
        catch (Exception ex)
        {
            _submittedInteractionCallId = null;
            _isSubmittingInteraction = false;
            SetSessionReady(false);
            IsGenerating.Value = false;
            StatusMessage.Value = $"Approval response failed ({ex.Message}). Reconnecting...";
            RequestRedraw();
            _ = ConnectUntilReadyAsync();
        }
    }

    private void EnqueuePendingInteraction(ToolInteractionRequest interaction)
    {
        if (_pendingInteractions.Any(pending => string.Equals(
                pending.CallId.Value,
                interaction.CallId.Value,
                StringComparison.Ordinal)))
        {
            return;
        }

        _pendingInteractions.Enqueue(interaction);
    }

    private void RemovePendingInteraction(string callId)
    {
        var remaining = _pendingInteractions
            .Where(pending => !string.Equals(pending.CallId.Value, callId, StringComparison.Ordinal))
            .ToArray();
        _pendingInteractions.Clear();
        foreach (var pending in remaining)
            _pendingInteractions.Enqueue(pending);
    }

    private void RefreshApprovalOptions()
    {
        _approvalOptions.Clear();
        if (CurrentInteraction is { } interaction)
        {
            foreach (var option in interaction.Options)
                _approvalOptions.Add(option.Label);
        }

        // Each new interaction opens collapsed so the user makes an explicit
        // choice to expand a long body — keeps controls visible by default.
        IsApprovalDetailVisible.Value = false;

        UiVersion.Value++;
    }

    private static string FormatReplyLetters(IReadOnlyList<ToolInteractionOption> options)
        => string.Join(", ", Enumerable.Range(0, options.Count).Select(i => GetReplyLetter(i)));

    private static string GetReplyLetter(int index)
        => ((char)('A' + index)).ToString();
}
