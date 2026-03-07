using System.Net.Http.Json;
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
    public const string TuiChannelType = "tui";

    internal static readonly TimeSpan[] DefaultReconnectDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    private readonly TimeSpan[] _reconnectDelays;
    private readonly HubConnection _connection;
    private readonly string _daemonEndpoint;
    private readonly string _hubUrl;
    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Subject<DaemonConnectionEvent> _connectionSubject = new();
    private readonly HttpClient _httpClient = new();
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _reconnectCtsLock = new();

    private string? _sessionId;
    private string? _channelType;
    private bool _hasConnected;
    private bool _disposed;
    private CancellationTokenSource? _reconnectCts;

    public DaemonClient(
        string daemonEndpoint,
        TimeProvider? timeProvider = null,
        TimeSpan[]? reconnectDelays = null,
        TimeSpan? serverTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(daemonEndpoint))
            throw new ArgumentException("Daemon endpoint cannot be empty.", nameof(daemonEndpoint));

        _daemonEndpoint = daemonEndpoint.TrimEnd('/');
        _hubUrl = BuildHubUrl(_daemonEndpoint);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reconnectDelays = reconnectDelays ?? DefaultReconnectDelays;

        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl)
            .WithAutomaticReconnect(_reconnectDelays)
            .Build();

        if (serverTimeout is { } timeout)
            _connection.ServerTimeout = timeout;

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

        // Cancel any in-flight background reconnect loop so it doesn't race
        // with this explicit connect attempt.
        CancelReconnectLoop();

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
                return;

            // Another reconnect/start sequence may already be in-flight
            // (e.g. the built-in auto-reconnect). Wait for it to settle.
            if (_connection.State is not HubConnectionState.Disconnected)
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
            foreach (var delay in _reconnectDelays)
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);

                try
                {
                    // Re-check state immediately before StartAsync. The auto-reconnect
                    // or a concurrent reconnect attempt may have changed the state since
                    // we last checked. StartAsync requires Disconnected state.
                    if (_connection.State is not HubConnectionState.Disconnected)
                    {
                        await WaitForStableConnectionStateAsync(cancellationToken);
                        if (IsConnected)
                        {
                            _hasConnected = true;
                            return;
                        }
                    }

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
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("is not in the Disconnected state", StringComparison.Ordinal))
                {
                    // The HubConnection state changed between our check and the
                    // StartAsync call (e.g. auto-reconnect kicked in concurrently).
                    // Wait for the state to settle and check if it connected.
                    await WaitForStableConnectionStateAsync(cancellationToken);
                    if (IsConnected)
                    {
                        _hasConnected = true;
                        return;
                    }

                    lastError = ex;
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
        var deadline = _timeProvider.GetUtcNow().AddSeconds(15);
        while (_timeProvider.GetUtcNow() < deadline)
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

    /// <summary>
    /// Queries the daemon REST API for recent sessions.
    /// Returns an empty list if the daemon is unreachable.
    /// </summary>
    public async Task<List<SessionCatalogEntryDto>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_daemonEndpoint}/api/sessions";
            var result = await _httpClient.GetFromJsonAsync<List<SessionCatalogEntryDto>>(
                url, cancellationToken);
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Sets the session ID for subsequent calls so that <c>EnsureSession</c>
    /// attaches to (or rehydrates) an existing session instead of creating a new one.
    /// </summary>
    public async Task<string> ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _channelType = TuiChannelType;
        _sessionId = sessionId;
        return await EnsureSessionInternalAsync(TuiChannelType, cancellationToken);
    }

    public async Task SendAsync(ChannelInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Hold the session gate while reading _sessionId to prevent reading
        // a stale value during a concurrent EnsureSessionInternalAsync call
        // (e.g. from the Reconnected handler racing with an explicit EnsureSessionAsync).
        string sessionId;
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await ConnectAsync(cancellationToken);

            sessionId = _sessionId
                ?? throw new InvalidOperationException(
                    "Session not initialized. Call CreateSessionAsync first.");
        }
        finally
        {
            _sessionGate.Release();
        }

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
        _disposed = true;
        _outputSubject.Dispose();
        _connectionSubject.Dispose();
        CancelReconnectLoop();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        await _connection.DisposeAsync();
        _httpClient.Dispose();
        _connectGate.Dispose();
        _sessionGate.Dispose();
    }

    /// <summary>
    /// Cancels any active reconnect loop. Called by <see cref="ConnectAsync"/>
    /// so an explicit caller supersedes background reconnect attempts and avoids
    /// two concurrent paths racing to call <c>StartAsync</c>.
    /// </summary>
    private void CancelReconnectLoop()
    {
        lock (_reconnectCtsLock)
        {
            if (_reconnectCts is { IsCancellationRequested: false } cts)
            {
                cts.Cancel();
                cts.Dispose();
                _reconnectCts = null;
            }
        }
    }

    private async Task ReconnectLoopAsync()
    {
        if (_disposed)
            return;

        CancellationTokenSource loopCts;
        lock (_reconnectCtsLock)
        {
            // If a previous reconnect loop is still alive, cancel it.
            if (_reconnectCts is { IsCancellationRequested: false } existing)
            {
                existing.Cancel();
                existing.Dispose();
            }

            loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _reconnectCts = loopCts;
        }

        var token = loopCts.Token;

        const int maxAttempts = 20;
        var attempts = 0;
        while (!_disposed && !token.IsCancellationRequested)
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

                await ReconnectConnectAsync(token);

                // Re-attach the session before publishing Connected. The
                // Reconnected handler already follows this order; mirroring it
                // here eliminates the race window where a test (or other caller)
                // observes Connected and calls EnsureSessionAsync concurrently
                // with this still-running EnsureSessionInternalAsync call.
                if (!string.IsNullOrWhiteSpace(_channelType))
                    await EnsureSessionInternalAsync(_channelType!, token);

                _connectionSubject.OnNext(new DaemonConnectionEvent(
                    DaemonConnectionState.Connected,
                    _daemonEndpoint,
                    $"Reconnected to daemon at {_daemonEndpoint}."));

                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The reconnect loop was superseded by an explicit ConnectAsync call
                // or the client is being disposed. Exit gracefully.
                return;
            }
            catch when (!_disposed && !token.IsCancellationRequested)
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

                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }
    }

    /// <summary>
    /// Internal connect path used by the reconnect loop. Unlike the public
    /// <see cref="ConnectAsync"/>, this does NOT call <see cref="CancelReconnectLoop"/>
    /// (which would cancel itself) and uses the semaphore normally.
    /// </summary>
    private async Task ReconnectConnectAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
            return;

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
                return;

            if (_connection.State is not HubConnectionState.Disconnected)
            {
                await WaitForStableConnectionStateAsync(cancellationToken);
                if (IsConnected)
                    return;
            }

            Exception? lastError = null;
            foreach (var delay in _reconnectDelays)
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);

                try
                {
                    if (_connection.State is not HubConnectionState.Disconnected)
                    {
                        await WaitForStableConnectionStateAsync(cancellationToken);
                        if (IsConnected)
                        {
                            _hasConnected = true;
                            return;
                        }
                    }

                    await _connection.StartAsync(cancellationToken);
                    _hasConnected = true;
                    return;
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("is not in the Disconnected state", StringComparison.Ordinal))
                {
                    await WaitForStableConnectionStateAsync(cancellationToken);
                    if (IsConnected)
                    {
                        _hasConnected = true;
                        return;
                    }

                    lastError = ex;
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

    private static string BuildHubUrl(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        return $"{trimmed}/hub/session";
    }

    private async Task<string> EnsureSessionInternalAsync(
        string channelType,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _sessionGate.Release();
        }
    }

    internal static SessionOutput FromDto(SessionOutputDto dto)
    {
        return SessionOutputDtoMapper.FromDto(dto);
    }
}
