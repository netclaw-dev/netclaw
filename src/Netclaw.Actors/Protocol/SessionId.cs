// -----------------------------------------------------------------------
// <copyright file="SessionId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Strongly-typed session identity. Wraps the entity key string used for
/// actor routing and persistence identity.
/// </summary>
public readonly record struct SessionId(string Value) : INetclawSerializableMessage
{
    public static explicit operator SessionId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Serializes <see cref="SessionId"/> as its bare primitive string so the
/// on-disk JSON form is byte-identical to the pre-value-object representation
/// (a raw <c>"sessionId"</c> string, never a nested <c>{ "Value": ... }</c> object).
/// </summary>
public sealed class SessionIdJsonConverter : JsonConverter<SessionId>
{
    public override SessionId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(
        Utf8JsonWriter writer, SessionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
