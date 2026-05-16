// -----------------------------------------------------------------------
// <copyright file="SenderId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Strongly-typed sender identity — the channel-level principal id of whoever
/// sent a message or initiated a turn. Wraps the raw id string so a sender id
/// cannot be confused with a session id, tool-call id, or any other string at
/// a call boundary.
/// </summary>
public readonly record struct SenderId(string Value)
{
    public static explicit operator SenderId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Serializes <see cref="SenderId"/> as its bare primitive string so the
/// on-disk JSON form is byte-identical to the pre-value-object representation
/// (a raw <c>"senderId"</c> string, never a nested <c>{ "Value": ... }</c> object).
/// </summary>
public sealed class SenderIdJsonConverter : JsonConverter<SenderId>
{
    public override SenderId Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(
        Utf8JsonWriter writer, SenderId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
