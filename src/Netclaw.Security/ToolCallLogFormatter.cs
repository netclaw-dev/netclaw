// -----------------------------------------------------------------------
// <copyright file="ToolCallLogFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Security;

/// <summary>
/// Makes the redacted and bounded text of a tool call for a log line that can
/// leave the host (OTLP export). Issue #2589.
/// </summary>
/// <remarks>
/// The order of the steps is a security property:
/// <list type="number">
/// <item>The walk replaces a value under a secret key, or a <see cref="SensitiveString"/>,
/// before it does anything else. A hash of a secret is not safe to export, because a
/// reader can confirm a guessed value with it. The <see cref="SensitiveString"/> JSON
/// converter writes the raw value, so this step must come before serialization.</item>
/// <item>The walk replaces a string longer than <see cref="MaxInlineStringChars"/> with
/// its length and a SHA-256 prefix, so file bodies do not leave the host.</item>
/// <item><see cref="SecretOutputRedactor.Redact"/> runs on the whole text.</item>
/// <item>The cap comes last. A cap before the redactor can cut a secret in half, and
/// the redactor then cannot match the part that stays.</item>
/// </list>
/// </remarks>
public static class ToolCallLogFormatter
{
    /// <summary>The maximum length of the arguments text, before the "..." suffix.</summary>
    public const int MaxArgumentsChars = 4096;

    /// <summary>A string value longer than this becomes a length and hash summary.</summary>
    public const int MaxInlineStringChars = 256;

    /// <summary>The maximum length of the rationale text, before the "..." suffix.</summary>
    public const int MaxRationaleChars = 500;

    // A deeper object or array becomes "<nested>". Without this guard, a cyclic
    // argument dictionary recurses until the stack overflows. 64 is the default
    // System.Text.Json reader depth, so a parsed model argument does not reach it.
    private const int MaxDepth = 64;

    private const string Redacted = "***REDACTED***";
    private const string DepthLimitMarker = "<nested>";
    private const string InvalidTextMarker = "<invalid UTF-16 text>";
    private const int HashPrefixChars = 16;

    // Relaxed escaping keeps '<' and '>' in the hash summary as they are, and it
    // keeps the text readable for an operator. The text goes to a log attribute,
    // not to HTML.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    /// <summary>
    /// Returns the arguments as compact JSON: secret values redacted, long strings
    /// summarized, the text redactor applied, and the text capped at
    /// <see cref="MaxArgumentsChars"/>. Null arguments give <c>{}</c>.
    /// </summary>
    public static string FormatArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return "{}";

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WritePairs(writer, arguments, depth: 0);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return TextTruncation.EllipsisAppend(SecretOutputRedactor.Redact(json), MaxArgumentsChars);
    }

    /// <summary>
    /// Returns the rationale with the text redactor applied, capped at
    /// <see cref="MaxRationaleChars"/>. Null gives the empty string.
    /// </summary>
    public static string FormatRationale(string? rationale)
    {
        if (rationale is null)
            return string.Empty;

        return TextTruncation.EllipsisAppend(SecretOutputRedactor.Redact(rationale), MaxRationaleChars);
    }

    private static void WritePairs(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<string, object?>> pairs,
        int depth)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in pairs)
        {
            writer.WritePropertyName(key);
            if (SecretOutputRedactor.IsSecretKey(key))
                writer.WriteStringValue(Redacted);
            else
                WriteValue(writer, value, depth + 1);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case SensitiveString:
                writer.WriteStringValue(Redacted);
                return;
            case string text:
                writer.WriteStringValue(Summarize(text));
                return;
            case JsonElement element:
                WriteElement(writer, element, depth);
                return;
        }

        if (depth > MaxDepth && value is IEnumerable and not byte[])
        {
            writer.WriteStringValue(DepthLimitMarker);
            return;
        }

        switch (value)
        {
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                WritePairs(writer, pairs, depth);
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = entry.Key.ToString() ?? string.Empty;
                    writer.WritePropertyName(key);
                    if (SecretOutputRedactor.IsSecretKey(key))
                        writer.WriteStringValue(Redacted);
                    else
                        WriteValue(writer, entry.Value, depth + 1);
                }

                writer.WriteEndObject();
                return;
            // byte[] goes to the opaque path, which writes one base64 string.
            case IEnumerable items and not byte[]:
                writer.WriteStartArray();
                foreach (var item in items)
                    WriteValue(writer, item, depth + 1);
                writer.WriteEndArray();
                return;
        }

        // A number, a bool, a JsonNode, or another object. Its JSON form goes
        // through the same element walk, so the key and length rules apply to it too.
        JsonElement opaque;
        try
        {
            opaque = JsonSerializer.SerializeToElement(value, value.GetType());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            writer.WriteStringValue($"<{value.GetType().Name} value unavailable>");
            return;
        }

        WriteElement(writer, opaque, depth);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when depth > MaxDepth:
            case JsonValueKind.Array when depth > MaxDepth:
                writer.WriteStringValue(DepthLimitMarker);
                return;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    // A name that does not decode cannot pass the secret-key check,
                    // so its value is redacted (fail closed).
                    var name = TryDecode(property, out var decoded) ? decoded : null;
                    writer.WritePropertyName(name ?? InvalidTextMarker);
                    if (name is null || SecretOutputRedactor.IsSecretKey(name))
                        writer.WriteStringValue(Redacted);
                    else
                        WriteElement(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item, depth + 1);
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(TryDecode(element, out var text) ? Summarize(text) : InvalidTextMarker);
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    // JSON text can hold an escaped lone surrogate ("\ud800"). The parser accepts it,
    // but the decode throws. The log line must not fail the tool call, so the walk
    // writes a marker for that value. The marker does not hold the raw text.
    private static bool TryDecode(JsonElement element, out string text)
    {
        try
        {
            text = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryDecode(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            return false;
        }
    }

    private static string Summarize(string text)
    {
        if (text.Length <= MaxInlineStringChars)
            return text;

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"<{text.Length} chars, sha256:{hash[..HashPrefixChars]}>";
    }
}
