// -----------------------------------------------------------------------
// <copyright file="TurnId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Strongly-typed turn correlation id — the unique identifier propagated across
/// session logs, actor boundaries, and memory checkpoints for end-to-end
/// traceability of a single turn. Distinct from <see cref="TurnNumber"/>: the
/// ordinal is a session-scoped counter, this is a globally-unique correlation
/// string. Wraps the raw id string so a turn id cannot be confused with a
/// session id, message id, or any other string at a call boundary.
/// </summary>
public readonly record struct TurnId(string Value)
{
    public static explicit operator TurnId(string value) => new(value);

    public override string ToString() => Value;
}
