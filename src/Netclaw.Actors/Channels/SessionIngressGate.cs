// -----------------------------------------------------------------------
// <copyright file="SessionIngressGate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Channels;

/// <summary>
/// Thrown when daemon-managed session ingress is closed during coordinated restart.
/// </summary>
public sealed class SessionIngressBlockedException : InvalidOperationException
{
    public SessionIngressBlockedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thread-safe gate used to reject new daemon-managed session ingress while coordinated restart is in progress.
/// </summary>
public sealed class SessionIngressGate
{
    public const string RestartInProgressMessage = "Daemon restarting, try again in a minute.";

    private string? _closedReason;

    public bool IsClosed => _closedReason is not null;

    public string? ClosedReason => Volatile.Read(ref _closedReason);

    public bool TryClose(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Interlocked.CompareExchange(ref _closedReason, reason, comparand: null) is null;
    }

    public void Reopen()
    {
        Interlocked.Exchange(ref _closedReason, null);
    }

    public void ThrowIfClosed()
    {
        var reason = ClosedReason;
        if (reason is not null)
            throw new SessionIngressBlockedException(reason);
    }
}
