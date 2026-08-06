// -----------------------------------------------------------------------
// <copyright file="FakeDaemonHubTransport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
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
    private Action<SessionOutputDto>? _outputHandler;

    /// <summary>Override to make <see cref="StartAsync"/> fail (throw) or delay.</summary>
    public Func<CancellationToken, Task>? StartHook { get; set; }

    /// <summary>
    /// Controls the EnsureSession result. The default creates a session on the
    /// first no-id call and re-attaches (echoes) a supplied id. Override to model
    /// a transient re-attach failure (throw) or a server restart (return a new id).
    /// </summary>
    public Func<object?[], SessionEnsureResultDto> EnsureSessionResponder { get; set; } = DefaultEnsureResponder();

    /// <summary>Override to make a value-less RPC (SendMessage / RespondToInteraction) delay or fail.</summary>
    public Func<string, object?[], CancellationToken, Task>? VoidInvokeHook { get; set; }

    public int StartAttempts { get; private set; }
    public int EnsureSessionCalls { get; private set; }
    public List<(string Method, object?[] Args)> Invocations { get; } = [];

    public bool IsConnected => _connected;

    public event Func<Exception?, Task>? Closed;

    public IDisposable On<TMessage>(string methodName, Action<TMessage> handler)
    {
        if (typeof(TMessage) == typeof(SessionOutputDto))
            _outputHandler = dto => handler((TMessage)(object)dto);

        return new Registration();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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

    public Task<TResult> InvokeAsync<TResult>(string methodName, object?[] args, CancellationToken cancellationToken)
    {
        Record(methodName, args);

        if (methodName == "EnsureSession")
            return Task.FromResult((TResult)(object)EnsureSessionResponder(args));

        throw new InvalidOperationException($"FakeDaemonHubTransport has no value result for '{methodName}'.");
    }

    public async Task InvokeAsync(string methodName, object?[] args, CancellationToken cancellationToken)
    {
        Record(methodName, args);

        if (VoidInvokeHook is not null)
            await VoidInvokeHook(methodName, args, cancellationToken);
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
        _ = Closed?.Invoke(error);
    }

    /// <summary>Pushes a server-to-client output through the registered handler.</summary>
    public void PushOutput(SessionOutputDto dto) => _outputHandler?.Invoke(dto);

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
                return new SessionEnsureResultDto(requested, false);

            var result = new SessionEnsureResultDto(createdId, !created);
            created = true;
            return result;
        };
    }

    private sealed class Registration : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
