// -----------------------------------------------------------------------
// <copyright file="TurnNumber.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Strongly-typed turn ordinal — the monotonically-increasing turn counter a
/// session uses to reject stale delivery feedback after a newer user turn has
/// started. Wraps the raw <see cref="int"/> so a turn number cannot be confused
/// with a token count, message count, or any other integer at a call boundary.
/// </summary>
public readonly record struct TurnNumber(int Value)
{
    public static explicit operator TurnNumber(int value) => new(value);

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Serializes a nullable <see cref="TurnNumber"/> as a bare primitive integer
/// or JSON <c>null</c> — used on optional DTO fields so a missing turn ordinal
/// stays a wire <c>null</c>, never a nested object. Mirrors the pre-value-object
/// <c>int?</c> representation exactly.
/// </summary>
public sealed class NullableTurnNumberJsonConverter : JsonConverter<TurnNumber?>
{
    public override TurnNumber? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : new TurnNumber(reader.GetInt32());

    public override void Write(
        Utf8JsonWriter writer, TurnNumber? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteNumberValue(value.Value.Value);
    }
}
