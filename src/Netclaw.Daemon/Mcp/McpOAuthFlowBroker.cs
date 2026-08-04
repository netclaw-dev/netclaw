// -----------------------------------------------------------------------
// <copyright file="McpOAuthFlowBroker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Web;
using ModelContextProtocol.Authentication;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed record McpOAuthFlowStart(
    McpOAuthFlow Flow,
    bool Created);

internal sealed record McpOAuthFlowTerminal(
    McpOAuthFlowStatus Status,
    McpErrorResponse? Error);

internal sealed class McpOAuthFlowBroker : IDisposable
{
    internal static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly Dictionary<McpServerName, McpOAuthFlow> _latestByServer = [];
    private readonly Dictionary<string, McpOAuthFlow> _byState = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _daemonCancellation;
    private bool _disposed;

    public McpOAuthFlowBroker(TimeProvider timeProvider, CancellationToken daemonCancellation)
    {
        _timeProvider = timeProvider;
        _daemonCancellation = daemonCancellation;
    }

    public McpOAuthFlowStart StartOrJoin(McpServerName serverName)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PruneTombstones();
            if (_latestByServer.TryGetValue(serverName, out var active))
            {
                if (!active.IsTerminal)
                    return new McpOAuthFlowStart(active, false);

                // A flow that failed before the SDK built its authorization URL never reached
                // _byState, so the state-keyed sweep below cannot reclaim it. Once it is
                // replaced here nothing can reach it at all, and its timer and its token
                // source registration on the daemon shutdown token would leak.
                if (active.State is null)
                    active.Dispose();
            }

            var flow = new McpOAuthFlow(
                serverName,
                _timeProvider.GetUtcNow().Add(FlowLifetime),
                _timeProvider,
                _daemonCancellation,
                OnFlowExpired,
                OnStateDiscovered);
            _latestByServer[serverName] = flow;
            return new McpOAuthFlowStart(flow, true);
        }
    }

    public bool TryGetActive(McpServerName serverName, out McpOAuthFlow? flow)
    {
        lock (_sync)
        {
            if (_latestByServer.TryGetValue(serverName, out flow) && !flow.IsTerminal)
                return true;
            flow = null;
            return false;
        }
    }

    public McpOAuthFlowStatus GetStatus(McpServerName serverName)
        => GetLatestStatus(serverName).Status;

    public McpOAuthFlowTerminal GetLatestStatus(McpServerName serverName)
    {
        lock (_sync)
        {
            PruneTombstones();
            return _latestByServer.TryGetValue(serverName, out var flow)
                ? flow.Terminal
                : new McpOAuthFlowTerminal(McpOAuthFlowStatus.NotStarted, null);
        }
    }

    public McpOAuthFlowTerminal GetStatusByState(string state)
    {
        lock (_sync)
        {
            PruneTombstones();
            return _byState.TryGetValue(state, out var flow)
                ? flow.Terminal
                : new McpOAuthFlowTerminal(McpOAuthFlowStatus.NotStarted, null);
        }
    }

    public McpOAuthFlow GetForCallback(string state)
    {
        lock (_sync)
        {
            PruneTombstones();
            if (!_byState.TryGetValue(state, out var flow))
                throw new McpOAuthCallbackException("The authorization state is unknown or no longer valid.");
            var now = _timeProvider.GetUtcNow();
            if (flow.ExpiresAt <= now && flow.ShouldExpire(now))
            {
                flow.Fail(new McpErrorResponse(
                    "Authorization expired. Start a new MCP authorization attempt.",
                    "callback validation"));
                throw new McpOAuthCallbackException("The authorization state has expired.");
            }

            if (flow.IsTerminal)
                throw new McpOAuthCallbackException("The authorization state has already been used.");
            return flow;
        }
    }

    public void Complete(McpOAuthFlow flow)
        => flow.Complete();

    /// <summary>
    /// Claims the unexpired flow for the non-cancellable commit sequence:
    /// durable credential commit, cache publication, then completion.
    /// </summary>
    public void BeginCommit(McpOAuthFlow flow)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!_latestByServer.TryGetValue(flow.ServerName, out var active)
                || !ReferenceEquals(active, flow)
                || !flow.TryBeginCommit(now))
            {
                if (flow.ShouldExpire(now))
                {
                    flow.Fail(new McpErrorResponse(
                        "Authorization expired before credentials could publish. Start a new attempt.",
                        "credential commit"));
                }

                throw new McpOAuthOperationException(new McpErrorResponse(
                    "Authorization can no longer publish. Start a new MCP authorization attempt.",
                    "credential commit"));
            }
        }
    }

    public void Fail(McpOAuthFlow flow, McpErrorResponse error)
        => flow.Fail(error);

    public void Dispose()
    {
        List<McpOAuthFlow> flows;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Union both indexes: a flow that never reached its authorization URL appears
            // only in _latestByServer.
            flows = _byState.Values.Concat(_latestByServer.Values).Distinct().ToList();
            _latestByServer.Clear();
            _byState.Clear();
        }

        foreach (var flow in flows)
        {
            flow.Fail(new McpErrorResponse("The daemon stopped the authorization flow.", "daemon shutdown"));
            flow.Dispose();
        }
    }

    /// <summary>
    /// Indexes a flow under the <c>state</c> value the MCP SDK generated for it. The browser
    /// callback carries only that value, so a flow cannot be routed to before the SDK builds
    /// its authorization URL. The flow registers here before it publishes that URL, so an
    /// operator can never be redirected to a callback the broker cannot resolve.
    /// </summary>
    private void OnStateDiscovered(McpOAuthFlow flow, string state)
    {
        lock (_sync)
        {
            // The SDK calls this from a task that outlives the broker. Dispose() clears both
            // indexes and fails every flow; re-inserting here would answer the operator's
            // callback with "already used" instead of the shutdown reason.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _byState[state] = flow;
        }
    }

    private void OnFlowExpired(McpOAuthFlow flow)
    {
        lock (_sync)
        {
            if (!flow.ShouldExpire(_timeProvider.GetUtcNow()))
                return;
            flow.Fail(new McpErrorResponse(
                "Authorization expired. Start a new MCP authorization attempt.",
                "authorization timeout"));
        }
    }

    private void PruneTombstones()
    {
        var cutoff = _timeProvider.GetUtcNow() - FlowLifetime;
        foreach (var (state, flow) in _byState.ToArray())
        {
            if (!flow.IsTerminal || flow.ExpiresAt > cutoff)
                continue;

            _byState.Remove(state);
            if (_latestByServer.TryGetValue(flow.ServerName, out var latest)
                && ReferenceEquals(latest, flow))
                _latestByServer.Remove(flow.ServerName);
            flow.Dispose();
        }
    }
}

internal sealed class McpOAuthFlow : IDisposable
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<(Uri Url, string State)> _authorizationRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AuthorizationResult> _authorizationResponse =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<McpOAuthFlowTerminal> _terminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetimeCancellation;
    private readonly ITimer _expiryTimer;
    private readonly Action<McpOAuthFlow, string> _stateDiscovered;
    private int _delegateOwner;
    private bool _codeDelivered;
    private bool _commitClaimed;
    private McpOAuthFlowTerminal _status = new(McpOAuthFlowStatus.Pending, null);

    public McpOAuthFlow(
        McpServerName serverName,
        DateTimeOffset expiresAt,
        TimeProvider timeProvider,
        CancellationToken daemonCancellation,
        Action<McpOAuthFlow> expired,
        Action<McpOAuthFlow, string> stateDiscovered)
    {
        ServerName = serverName;
        ExpiresAt = expiresAt;
        _stateDiscovered = stateDiscovered;
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(daemonCancellation);
        _expiryTimer = timeProvider.CreateTimer(
            static state =>
            {
                var callback = (ExpiryCallback)state!;
                callback.Expired(callback.Flow);
            },
            new ExpiryCallback(this, expired),
            McpOAuthFlowBroker.FlowLifetime,
            Timeout.InfiniteTimeSpan);
    }

    public McpServerName ServerName { get; }

    /// <summary>
    /// The <c>state</c> value the MCP SDK generated for this flow. Null until the SDK builds
    /// the authorization URL.
    /// </summary>
    public string? State { get; private set; }

    public DateTimeOffset ExpiresAt { get; }

    public CancellationToken CancellationToken => _lifetimeCancellation.Token;

    public bool IsTerminal => _terminal.Task.IsCompleted;

    public McpOAuthFlowTerminal Terminal
    {
        get
        {
            lock (_sync)
                return _status;
        }
    }

    public Task<(Uri Url, string State)> WaitForAuthorizationRequestAsync(CancellationToken requestCancellation)
        => _authorizationRequest.Task.WaitAsync(requestCancellation);

    public Task<McpOAuthFlowTerminal> WaitForTerminalAsync(CancellationToken requestCancellation)
        => _terminal.Task.WaitAsync(requestCancellation);

    public async Task<AuthorizationResult?> HandleAuthorizationCallbackAsync(
        AuthorizationCallbackContext context,
        CancellationToken sdkCancellation)
    {
        if (Interlocked.CompareExchange(ref _delegateOwner, 1, 0) != 0)
            throw new McpOAuthAuthorizationInProgressException(ServerName);

        PublishAuthorizationRequest(context.AuthorizationUri);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            sdkCancellation,
            _lifetimeCancellation.Token);
        return await _authorizationResponse.Task.WaitAsync(cancellation.Token);
    }

    /// <summary>
    /// Hands the browser's callback parameters back to the MCP SDK. The SDK compares
    /// <paramref name="state"/> against the value it generated and validates
    /// <paramref name="iss"/> per RFC 9207, so both travel through unmodified.
    /// </summary>
    public void DeliverAuthorizationResponse(string code, string? state, string? iss)
    {
        lock (_sync)
        {
            if (_terminal.Task.IsCompleted || _codeDelivered)
                throw new McpOAuthCallbackException("The authorization state has already been used.");
            _codeDelivered = true;
        }

        _authorizationResponse.TrySetResult(new AuthorizationResult
        {
            Code = code,
            State = state,
            Iss = iss,
        });
    }

    private void PublishAuthorizationRequest(Uri authorizationUri)
    {
        var state = HttpUtility.ParseQueryString(authorizationUri.Query)["state"];
        if (string.IsNullOrEmpty(state))
        {
            var error = new McpErrorResponse(
                "The MCP SDK produced an authorization URL with no state parameter.",
                "authorization start");
            // Fail before throwing. The caller waiting in StartAuthorizationAsync observes
            // _authorizationRequest, and an exception that only unwinds the SDK's task would
            // leave that caller blocked for the full flow lifetime.
            Fail(error);
            throw new McpOAuthOperationException(error);
        }

        lock (_sync)
            State = state;

        // Index before publishing the URL: the operator cannot reach the callback until the
        // URL is observable, so this ordering makes an unroutable callback impossible.
        _stateDiscovered(this, state);
        _authorizationRequest.TrySetResult((authorizationUri, state));
    }

    public bool TryBeginCommit(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_terminal.Task.IsCompleted
                || _commitClaimed
                || !_codeDelivered
                || now >= ExpiresAt)
                return false;
            _commitClaimed = true;
            _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    public bool ShouldExpire(DateTimeOffset now)
    {
        lock (_sync)
            return !_terminal.Task.IsCompleted && !_commitClaimed && now >= ExpiresAt;
    }

    public void Complete()
    {
        var terminal = new McpOAuthFlowTerminal(McpOAuthFlowStatus.Completed, null);
        lock (_sync)
        {
            if (_terminal.Task.IsCompleted)
                return;
            if (!_commitClaimed)
                throw new InvalidOperationException("OAuth flow completed without claiming its commit transition.");
            _status = terminal;
            _terminal.TrySetResult(terminal);
        }
        EndLifetime();
    }

    public void Fail(McpErrorResponse error)
    {
        var terminal = new McpOAuthFlowTerminal(McpOAuthFlowStatus.Failed, error);
        lock (_sync)
        {
            if (_terminal.Task.IsCompleted)
                return;
            _status = terminal;
            _terminal.TrySetResult(terminal);
        }

        _authorizationRequest.TrySetException(new McpOAuthOperationException(error));
        _authorizationResponse.TrySetCanceled(_lifetimeCancellation.Token);
        EndLifetime();
    }

    /// <summary>Observable so a test can prove the broker reclaims a retired flow.</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        _expiryTimer.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void EndLifetime()
    {
        _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    private sealed record ExpiryCallback(McpOAuthFlow Flow, Action<McpOAuthFlow> Expired);
}

internal sealed class McpOAuthOperationException(McpErrorResponse error) : Exception(error.Error)
{
    public McpErrorResponse Error { get; } = error;
}

internal sealed class McpOAuthCallbackException(string message) : Exception(message);

internal sealed class McpOAuthAuthorizationInProgressException(McpServerName serverName)
    : Exception($"OAuth authorization is already in progress for MCP server '{serverName.Value}'.");

internal enum McpOAuthFlowStatus
{
    NotStarted,
    Pending,
    Completed,
    Failed,
}
