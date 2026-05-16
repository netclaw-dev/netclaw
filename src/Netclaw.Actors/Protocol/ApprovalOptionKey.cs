// -----------------------------------------------------------------------
// <copyright file="ApprovalOptionKey.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Strongly-typed tool-approval option key — the stable wire discriminator a
/// channel adapter renders as a button and the user's selection routes back on
/// <see cref="ToolInteractionResponse.SelectedKey"/>. Wraps the raw key string
/// so an option key cannot be confused with a session id, tool-call id, or any
/// other string at a call boundary. The canonical keys are exposed as named
/// factories on <see cref="ApprovalOptionKeys"/>.
/// </summary>
/// <remarks>
/// Carries a <see cref="JsonConverter"/> attribute because
/// <see cref="ToolInteractionOption"/> values cross the SignalR JSON boundary
/// nested inside <c>SessionOutputDto.InteractionOptions</c>; the converter keeps
/// the wire form a bare string rather than a <c>{ "Value": ... }</c> object.
/// </remarks>
[JsonConverter(typeof(ApprovalOptionKeyJsonConverter))]
public readonly record struct ApprovalOptionKey(string Value)
{
    public static explicit operator ApprovalOptionKey(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Serializes <see cref="ApprovalOptionKey"/> as its bare primitive string so
/// the on-wire JSON form is byte-identical to the pre-value-object
/// representation (a raw <c>"key"</c> string, never a nested object).
/// </summary>
public sealed class ApprovalOptionKeyJsonConverter : JsonConverter<ApprovalOptionKey>
{
    public override ApprovalOptionKey Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(
        Utf8JsonWriter writer, ApprovalOptionKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
