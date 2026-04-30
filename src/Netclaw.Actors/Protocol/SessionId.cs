// -----------------------------------------------------------------------
// <copyright file="SessionId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Strongly-typed session identity. Wraps the entity key string used for
/// actor routing and persistence identity.
/// </summary>
public readonly record struct SessionId(string Value)
{
    public static explicit operator SessionId(string value) => new(value);

    public override string ToString() => Value;
}
