// -----------------------------------------------------------------------
// <copyright file="SourceScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Channels;

/// <summary>
/// Strongly-typed source scope identifier — the optional provenance marker on
/// <see cref="SourceProvenance"/> naming the repository, environment, or tenant
/// a message originated from. Wraps the raw string so a scope identifier cannot
/// be confused with a <see cref="SourceKind"/> or any other string at a call
/// boundary.
/// </summary>
public readonly record struct SourceScope(string Value)
{
    public static explicit operator SourceScope(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Strongly-typed source kind identifier — the optional provenance marker on
/// <see cref="SourceProvenance"/> naming the source object (such as a webhook
/// event type) a message originated from. Wraps the raw string so a source-kind
/// identifier cannot be confused with a <see cref="SourceScope"/> or any other
/// string at a call boundary.
/// </summary>
public readonly record struct SourceKind(string Value)
{
    public static explicit operator SourceKind(string value) => new(value);

    public override string ToString() => Value;
}
