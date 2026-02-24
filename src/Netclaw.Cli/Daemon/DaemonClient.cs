using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Thin SignalR client for daemon-backed sessions.
/// Maintains connection state, session attachment across reconnects,
/// and exposes mapped <see cref="SessionOutput"/> events for the TUI.
/// </summary>
public sealed class DaemonClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    private readonly HubConnection _connection;
    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Subject<DaemonConnectionEvent> _connectionSubject = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private string? _sessionId;
    private bool _hasConnected;
    private bool _disposed;

    public DaemonClient(string daemonEndpoint)
    {
        if (string.IsNullOrWhiteSpace(daemonEndpoint))
            throw new ArgumentException("Daemon endpoint cannot be empty.", nameof(daemonEndpoint));

        var hubUrl = BuildHubUrl(daemonEndpoint);

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();

        _connection.On<SessionOutputDto>("ReceiveOutput", dto =>
        {
            _outputSubject.OnNext(FromDto(dto));
        });

        _connection.Reconnected += async _ =>
        {
            var sessionId = _sessionId;
            if (!string.IsNullOrWhiteSpace(sessionId))
                await _connection.InvokeCoreAsync("AttachSession", [sessionId]);

            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Connected,
                "Reconnected to daemon."));
        };

        _connection.Reconnecting += ex =>
        {
            var reason = ex?.Message ?? "connection dropped";
            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Reconnecting,
                $"Reconnecting to daemon: {reason}"));
            return Task.CompletedTask;
        };

        _connection.Closed += async ex =>
        {
            if (_disposed)
                return;

            var reason = ex?.Message ?? "connection closed";
            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Disconnected,
                $"Disconnected from daemon: {reason}"));

            if (!string.IsNullOrWhiteSpace(_sessionId))
                await ReconnectLoopAsync();
        };
    }

    public IObservable<SessionOutput> SessionOutput => _outputSubject.AsObservable();
    public IObservable<DaemonConnectionEvent> ConnectionEvents => _connectionSubject.AsObservable();

    public bool IsConnected => _connection.State is HubConnectionState.Connected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
                return;

            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Connecting,
                "Connecting to daemon..."));

            Exception? lastError = null;
            foreach (var delay in ReconnectDelays)
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);

                try
                {
                    await _connection.StartAsync(cancellationToken);

                    var sessionId = _sessionId;
                    if (!string.IsNullOrWhiteSpace(sessionId))
                        await _connection.InvokeCoreAsync("AttachSession", [sessionId], cancellationToken);

                    _connectionSubject.OnNext(new DaemonConnectionEvent(
                        DaemonConnectionState.Connected,
                        _hasConnected ? "Reconnected to daemon." : "Connected to daemon."));
                    _hasConnected = true;
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException("Failed to connect to daemon SignalR hub.", lastError);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<string> CreateSessionAsync(
        string channelType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelType))
            throw new ArgumentException("Channel type cannot be empty.", nameof(channelType));

        await ConnectAsync(cancellationToken);
        var sessionId = await _connection.InvokeCoreAsync<string>(
            "CreateSession",
            [channelType],
            cancellationToken);

        _sessionId = sessionId;
        return sessionId;
    }

    public async Task SendAsync(ChannelInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ConnectAsync(cancellationToken);

        var sessionId = _sessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("Session not initialized. Call CreateSessionAsync first.");

        var text = input.Contents.OfType<TextContent>().Select(x => x.Text).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Only non-empty text messages are currently supported.");

        await _connection.InvokeCoreAsync(
            "SendMessage",
            [sessionId, text],
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _outputSubject.Dispose();
        _connectionSubject.Dispose();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _disposed = true;
        await _connection.DisposeAsync();
        _connectGate.Dispose();
    }

    private async Task ReconnectLoopAsync()
    {
        if (_disposed)
            return;

        var attempts = 0;
        while (!_disposed && !_lifetimeCts.Token.IsCancellationRequested)
        {
            attempts++;
            try
            {
                await ConnectAsync(_lifetimeCts.Token);
                return;
            }
            catch when (!_disposed && !_lifetimeCts.Token.IsCancellationRequested)
            {
                if (attempts >= 20)
                {
                    _connectionSubject.OnNext(new DaemonConnectionEvent(
                        DaemonConnectionState.Disconnected,
                        "Unable to reconnect to daemon after multiple attempts."));
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
            }
        }
    }

    private static string BuildHubUrl(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        return $"{trimmed}/hub/session";
    }

    internal static SessionOutput FromDto(SessionOutputDto dto)
    {
        var sessionId = new SessionId(dto.SessionId);

        return dto.Type switch
        {
            "text" => new TextOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Text = dto.Text ?? string.Empty
            },
            "thinking" => new ThinkingOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Text = dto.Text ?? string.Empty
            },
            "tool_call" => new ToolCallOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = dto.CallId ?? string.Empty,
                ToolName = dto.ToolName ?? "unknown",
                ArgumentsJson = dto.ArgumentsJson
            },
            "tool_result" => new ToolResultOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                CallId = dto.CallId ?? string.Empty,
                ToolName = dto.ToolName ?? "unknown",
                Result = dto.Result ?? string.Empty
            },
            "usage" => new UsageOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                InputTokens = dto.InputTokens,
                OutputTokens = dto.OutputTokens,
                TotalTokens = dto.TotalTokens,
                ContextWindowTokens = dto.ContextWindowTokens ?? 0,
                UsagePercent = dto.UsagePercent
            },
            "turn_completed" => new TurnCompleted
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                TurnNumber = dto.TurnNumber ?? 0
            },
            "session_title" => new SessionTitleOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Title = dto.Title ?? string.Empty
            },
            "error" => new ErrorOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Message = dto.ErrorMessage ?? "Unknown daemon error"
            },
            "compaction" => new CompactionOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                MessagesBefore = dto.MessagesBefore ?? 0,
                MessagesAfter = dto.MessagesAfter ?? 0
            },
            "session_joined" => new SessionJoined
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Title = dto.Title,
                TurnCount = dto.TurnCount ?? 0
            },
            _ => new ErrorOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Message = $"Unknown output type from daemon: {dto.Type}"
            }
        };
    }
}
