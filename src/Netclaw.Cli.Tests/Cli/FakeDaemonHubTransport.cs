// -----------------------------------------------------------------------
// <copyright file="FakeDaemonHubTransport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using Netclaw.Actors.Channels;
using static Netclaw.Cli.Daemon.ChatClientProtocol;
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// A controllable <see cref="IDaemonHubTransport"/> for deterministic
/// <see cref="DaemonClient"/> tests. The test drives the transport seam
/// directly — start success/failure, RPC completion, drops, output pushes —
/// so the reconnect and session state machine runs with no sockets, no ports,
/// and no wall-clock timing.
/// </summary>
internal sealed class FakeDaemonHubTransport : IDaemonHubTransport
{
    private readonly object _gate = new();
    private volatile bool _connected;
    private Action<TransportEvent>? _notify;
    private int _active;
    public int PeakConcurrency { get; private set; }

    /// <summary>Override to make <see cref="StartAsync"/> fail (throw) or delay.</summary>
    public Func<CancellationToken, Task>? StartHook { get; set; }

    /// <summary>
    /// Controls the EnsureSession result. The default creates a session on the
    /// first no-id call and re-attaches (echoes) a supplied id. Override to model
    /// a transient re-attach failure (throw) or a server restart (return a new id).
    /// </summary>
    public Func<object?[], SessionEnsureResultDto> EnsureSessionResponder { get; set; } = DefaultEnsureResponder();

    /// <summary>
    /// Awaited before every EnsureSession RPC is answered. A test completes it later to hold a
    /// session set-up in flight without blocking a thread.
    /// </summary>
    public Func<object?[], Task>? EnsureSessionGate { get; set; }

    /// <summary>Override to make a value-less RPC (SendMessage / RespondToInteraction) delay or fail.</summary>
    public Func<string, object?[], CancellationToken, Task>? VoidInvokeHook { get; set; }

    public int StartAttempts { get; private set; }
    public int EnsureSessionCalls { get; private set; }
    public List<(string Method, object?[] Args)> Invocations { get; } = [];

    public bool IsConnected => _connected;

    public IDisposable Subscribe(Action<TransportEvent> notify)
    {
        _notify = notify;
        return new Registration(() => _notify = null);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var operation = BeginOperation();
        lock (_gate)
            StartAttempts++;

        // Mirror SignalR: StartAsync on a live connection is illegal. This makes a
        // "reconnect re-calls StartAsync while still connected" defect observable.
        if (_connected)
            throw new InvalidOperationException(
                "The HubConnection cannot be started while it is not in the 'Disconnected' state.");

        if (StartHook is not null)
            await StartHook(cancellationToken);

        _connected = true;
    }

    public async Task<SessionBinding> EnsureSessionAsync(SessionId? sessionId, ChannelType channel, CancellationToken cancellationToken)
    {
        using var operation = BeginOperation();
        object?[] args = [sessionId?.Value, channel.ToWireValue()];
        Record("EnsureSession", args);
        if (EnsureSessionGate is not null) await EnsureSessionGate(args);
        var result = EnsureSessionResponder(args);
        return new SessionBinding(new SessionId(result.SessionId), result.TextAdmissionVersion);
    }

    public Task SendAsync(SessionId sessionId, string text, CancellationToken cancellationToken)
        => InvokeAsync("SendMessage", [sessionId.Value, text], cancellationToken);

    public Task RespondAsync(SessionId sessionId, ToolCallId callId, ApprovalOptionKey selection, CancellationToken cancellationToken)
        => InvokeAsync("RespondToInteraction", [sessionId.Value, callId.Value, selection.Value], cancellationToken);

    private async Task InvokeAsync(string methodName, object?[] args, CancellationToken cancellationToken)
    {
        using var operation = BeginOperation();
        Record(methodName, args);
        if (VoidInvokeHook is not null) await VoidInvokeHook(methodName, args, cancellationToken);
    }

    private IDisposable BeginOperation()
    {
        lock (_gate) { _active++; PeakConcurrency = Math.Max(PeakConcurrency, _active); }
        return new Registration(() => { lock (_gate) _active--; });
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>Simulates a transport drop and notifies the owner.</summary>
    public void RaiseClosed(Exception? error = null)
    {
        _connected = false;
        _notify?.Invoke(new TransportDropped(error));
    }

    /// <summary>Pushes a server-to-client output through the registered handler.</summary>
    public void PushOutput(SessionOutputDto dto) => _notify?.Invoke(new OutputReceived(SessionOutputDtoMapper.FromDto(dto)));

    private void Record(string methodName, object?[] args)
    {
        lock (_gate)
        {
            Invocations.Add((methodName, args));
            if (methodName == "EnsureSession")
                EnsureSessionCalls++;
        }
    }

    // Default: the first EnsureSession with no prior id creates a session;
    // later calls (a supplied id) re-attach and return the same id.
    private static Func<object?[], SessionEnsureResultDto> DefaultEnsureResponder()
    {
        const string createdId = "fake/session";
        var created = false;
        return args =>
        {
            if (args[0] is string requested)
                return new SessionEnsureResultDto(requested, false) { TextAdmissionVersion = 1 };

            var result = new SessionEnsureResultDto(createdId, !created) { TextAdmissionVersion = 1 };
            created = true;
            return result;
        };
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
