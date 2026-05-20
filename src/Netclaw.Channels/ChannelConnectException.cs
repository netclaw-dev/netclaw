// -----------------------------------------------------------------------
// <copyright file="ChannelConnectException.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Classifies why a channel transport failed to connect.
/// </summary>
public enum ChannelConnectFailureKind
{
    /// <summary>
    /// A transient failure — network blip, transient gateway error, upstream
    /// outage. Retrying the connection later is expected to succeed.
    /// </summary>
    Transient,

    /// <summary>
    /// A fatal misconfiguration — bad token, missing/disallowed gateway intents,
    /// missing OAuth scope. Retrying will not help until an operator corrects
    /// the configuration, so the channel stays disconnected without reconnecting.
    /// </summary>
    Fatal,
}

/// <summary>
/// Raised by a channel transport when an initial connection attempt fails. Carries
/// a <see cref="ChannelConnectFailureKind"/> so the hosting channel can decide
/// whether to retry (transient) or stay degraded (fatal) — and an operator-facing
/// <see cref="System.Exception.Message"/> suitable for alerts and <c>netclaw status</c>.
/// </summary>
public sealed class ChannelConnectException : Exception
{
    public ChannelConnectException(
        ChannelConnectFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ChannelConnectFailureKind Kind { get; }

    public bool IsFatal => Kind == ChannelConnectFailureKind.Fatal;
}
