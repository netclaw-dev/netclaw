using Microsoft.AspNetCore.SignalR.Client;
using R3;
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
    private readonly string _daemonEndpoint;
    private readonly string _hubUrl;
    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Subject<DaemonConnectionEvent> _connectionSubject = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private string? _sessionId;
    private string? _channelType;
    private bool _hasConnected;
    private bool _disposed;

    public DaemonClient(string daemonEndpoint)
    {
        if (string.IsNullOrWhiteSpace(daemonEndpoint))
            throw new ArgumentException("Daemon endpoint cannot be empty.", nameof(daemonEndpoint));

        _daemonEndpoint = daemonEndpoint.TrimEnd('/');
        _hubUrl = BuildHubUrl(_daemonEndpoint);

        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl)
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();

        _connection.On<SessionOutputDto>("ReceiveOutput", dto =>
        {
            _outputSubject.OnNext(FromDto(dto));
        });

        _connection.Reconnected += async _ =>
        {
            if (!string.IsNullOrWhiteSpace(_channelType))
                await EnsureSessionInternalAsync(_channelType!, CancellationToken.None);

            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Connected,
                _daemonEndpoint,
                $"Reconnected to daemon at {_daemonEndpoint}."));
        };

        _connection.Reconnecting += ex =>
        {
            var reason = ex?.Message ?? "connection dropped";
            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Reconnecting,
                _daemonEndpoint,
                $"Reconnecting to {_daemonEndpoint}: {reason}"));
            return Task.CompletedTask;
        };

        _connection.Closed += async ex =>
        {
            if (_disposed)
                return;

            var reason = ex?.Message ?? "connection closed";
            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Disconnected,
                _daemonEndpoint,
                $"Disconnected from daemon at {_daemonEndpoint}: {reason}"));

            if (!string.IsNullOrWhiteSpace(_sessionId))
                await ReconnectLoopAsync();
        };
    }

    public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();
    public Observable<DaemonConnectionEvent> ConnectionEvents => _connectionSubject.AsObservable();

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

            // Another reconnect/start sequence may already be in-flight.
            if (_connection.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                await WaitForStableConnectionStateAsync(cancellationToken);
                if (IsConnected)
                    return;
            }

            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Connecting,
                _daemonEndpoint,
                $"Connecting to daemon at {_daemonEndpoint}..."));

            Exception? lastError = null;
            foreach (var delay in ReconnectDelays)
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);

                try
                {
                    await _connection.StartAsync(cancellationToken);

                    _connectionSubject.OnNext(new DaemonConnectionEvent(
                        DaemonConnectionState.Connected,
                        _daemonEndpoint,
                        _hasConnected
                            ? $"Reconnected to daemon at {_daemonEndpoint}."
                            : $"Connected to daemon at {_daemonEndpoint}."));
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

    private async Task WaitForStableConnectionStateAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_connection.State is HubConnectionState.Connected or HubConnectionState.Disconnected)
                return;

            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task<string> CreateSessionAsync(
        string channelType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelType))
            throw new ArgumentException("Channel type cannot be empty.", nameof(channelType));

        _channelType = channelType;
        _sessionId = null;
        return await EnsureSessionInternalAsync(channelType, cancellationToken);
    }

    public async Task<string> EnsureSessionAsync(
        string channelType,
        CancellationToken cancellationToken = default)
    {
        _channelType = channelType;
        return await EnsureSessionInternalAsync(channelType, cancellationToken);
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

        const int maxAttempts = 20;
        var attempts = 0;
        while (!_disposed && !_lifetimeCts.Token.IsCancellationRequested)
        {
            attempts++;
            try
            {
                _connectionSubject.OnNext(new DaemonConnectionEvent(
                    DaemonConnectionState.Reconnecting,
                    _daemonEndpoint,
                    $"Retrying daemon connection at {_daemonEndpoint} (attempt {attempts}/{maxAttempts})...",
                    attempts,
                    maxAttempts,
                    0));

                await ConnectAsync(_lifetimeCts.Token);
                return;
            }
            catch when (!_disposed && !_lifetimeCts.Token.IsCancellationRequested)
            {
                if (attempts >= maxAttempts)
                {
                    _connectionSubject.OnNext(new DaemonConnectionEvent(
                        DaemonConnectionState.Disconnected,
                        _daemonEndpoint,
                        $"Unable to reconnect to daemon at {_daemonEndpoint} after {maxAttempts} attempts.",
                        attempts,
                        maxAttempts,
                        0));
                    return;
                }

                for (var countdown = 2; countdown > 0; countdown--)
                {
                    _connectionSubject.OnNext(new DaemonConnectionEvent(
                        DaemonConnectionState.Reconnecting,
                        _daemonEndpoint,
                        $"Retrying daemon connection at {_daemonEndpoint} (attempt {attempts + 1}/{maxAttempts}) in {countdown}s...",
                        attempts + 1,
                        maxAttempts,
                        countdown));

                    await Task.Delay(TimeSpan.FromSeconds(1), _lifetimeCts.Token);
                }
            }
        }
    }

    private static string BuildHubUrl(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        return $"{trimmed}/hub/session";
    }

    private async Task<string> EnsureSessionInternalAsync(
        string channelType,
        CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);

        var result = await _connection.InvokeCoreAsync<SessionEnsureResultDto>(
            "EnsureSession",
            [_sessionId, channelType],
            cancellationToken);

        _sessionId = result.SessionId;

        if (result.Created)
        {
            _connectionSubject.OnNext(new DaemonConnectionEvent(
                DaemonConnectionState.Connected,
                _daemonEndpoint,
                $"Created a new daemon session at {_daemonEndpoint}."));
        }

        return result.SessionId;
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
            "text_delta" => new TextDeltaOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Delta = dto.Text ?? string.Empty
            },
            "thinking" => new ThinkingOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Text = dto.Text ?? string.Empty
            },
            "thinking_delta" => new ThinkingDeltaOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                Delta = dto.Text ?? string.Empty
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
                Message = dto.ErrorMessage ?? "Unknown daemon error",
                Cause = dto.ErrorDetail is not null
                    ? new Exception(dto.ErrorDetail) : null
            },
            "file" => new FileOutput
            {
                SessionId = sessionId,
                TimestampMs = dto.TimestampMs,
                FilePath = dto.FilePath ?? string.Empty,
                FileName = dto.FileName ?? "file",
                MimeType = dto.MimeType ?? "application/octet-stream"
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
