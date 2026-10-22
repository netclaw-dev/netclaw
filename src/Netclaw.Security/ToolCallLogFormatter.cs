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
/// reader can confirm a guessed value with it.</item>
/// <item>The walk writes only known types. Any other value becomes "&lt;TypeName&gt;",
/// because a serializer can write a secret that the walk cannot see.</item>
/// <item><see cref="SecretOutputRedactor.Redact"/> runs on each string value. In the
/// serialized text, the quotes of a JSON body inside a string are escaped, and the
/// JSON rule of the redactor cannot match them.</item>
/// <item>A string longer than <see cref="MaxInlineStringChars"/> becomes its length and
/// a SHA-256 prefix, so file bodies do not leave the host. When the redactor changed
/// that string, it gets no hash.</item>
/// <item><see cref="SecretOutputRedactor.Redact"/> runs again on the whole text.</item>
/// <item>The cap comes last. A cap before the redactor can cut a secret in half, and
/// the redactor then cannot match the part that stays.</item>
/// </list>
/// The formatter never throws. The log line comes before the tool runs, so an
/// exception here would fail the tool call.
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

    // The walk stops adding items after this many bytes. The depth guard alone does
    // not bound the work: a list that holds itself twice doubles the nodes at each
    // level. The budget is four times the cap, so the cap still cuts the text after
    // the final redactor pass.
    private const int WalkBudgetBytes = 4 * MaxArgumentsChars;

    private const string Redacted = "***REDACTED***";
    private const string DepthLimitMarker = "<nested>";
    private const string UnavailableMarker = "<arguments unavailable>";
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
    /// <see cref="MaxArgumentsChars"/>. Null arguments give <c>{}</c>. When the
    /// arguments cannot be read, the result is <c>&lt;arguments unavailable&gt;</c>.
    /// </summary>
    public static string FormatArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return "{}";

        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                WritePairs(writer, arguments, depth: 0);
            }

            var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
            return TextTruncation.EllipsisAppend(SecretOutputRedactor.Redact(json), MaxArgumentsChars);
        }
        catch (Exception)
        {
            // A model or MCP argument can hold anything: an enumerator that throws, an
            // escaped lone surrogate, an undefined JsonElement. The partial text is
            // dropped, so nothing unredacted leaves, and the tool call still runs.
            return UnavailableMarker;
        }
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

    private static bool OverBudget(Utf8JsonWriter writer)
        => writer.BytesCommitted + writer.BytesPending > WalkBudgetBytes;

    private static void WritePairs(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<string, object?>> pairs,
        int depth)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in pairs)
        {
            if (OverBudget(writer))
                break;

            writer.WritePropertyName(key);
            if (SecretOutputRedactor.IsSecretKey(key))
                writer.WriteStringValue(Redacted);
            else
                WriteValue(writer, value, depth + 1);
        }

        writer.WriteEndObject();
    }

    // Only these types are written. Every other value becomes "<TypeName>" and is
    // not read, so its serializer, getters, and keys cannot leak or throw.
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
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case int number:
                writer.WriteNumberValue(number);
                return;
            case long number:
                writer.WriteNumberValue(number);
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case double number when double.IsFinite(number):
                writer.WriteNumberValue(number);
                return;
            case IEnumerable<KeyValuePair<string, object?>> or IList when depth > MaxDepth:
                writer.WriteStringValue(DepthLimitMarker);
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                WritePairs(writer, pairs, depth);
                return;
            case IList items and not byte[]:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    if (OverBudget(writer))
                        break;

                    WriteValue(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue($"<{value.GetType().Name}>");
                return;
        }
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object or JsonValueKind.Array when depth > MaxDepth:
                writer.WriteStringValue(DepthLimitMarker);
                return;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (OverBudget(writer))
                        break;

                    writer.WritePropertyName(property.Name);
                    if (SecretOutputRedactor.IsSecretKey(property.Name))
                        writer.WriteStringValue(Redacted);
                    else
                        WriteElement(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    if (OverBudget(writer))
                        break;

                    WriteElement(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(Summarize(element.GetString() ?? string.Empty));
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private static string Summarize(string text)
    {
        var redacted = SecretOutputRedactor.Redact(text);
        if (text.Length <= MaxInlineStringChars)
            return redacted;

        // A hash of a string that held a secret lets a reader confirm a guessed
        // secret when the rest of the string is a known template.
        if (!string.Equals(redacted, text, StringComparison.Ordinal))
            return $"<{text.Length} chars, redacted>";

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"<{text.Length} chars, sha256:{hash[..HashPrefixChars]}>";
    }
}
