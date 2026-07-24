// -----------------------------------------------------------------------
// <copyright file="SensitiveString.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Configuration;

/// <summary>
/// Wraps a secret value (API key, OAuth token) to prevent accidental
/// exposure in logs, debug output, or string interpolation.
/// Access the inner value explicitly via <see cref="Value"/>.
/// </summary>
/// <param name="Value">The secret value.</param>
[TypeConverter(typeof(SensitiveStringTypeConverter))]
[JsonConverter(typeof(SensitiveStringJsonConverter))]
public sealed record SensitiveString(string Value)
{
    public override string ToString() => "***REDACTED***";
}

public static class SensitiveStringExtensions
{
    /// <summary>
    /// Returns true when <paramref name="token"/> is null or wraps a null/whitespace value.
    /// </summary>
    public static bool IsNullOrEmpty([NotNullWhen(false)] this SensitiveString? token)
        => token is null || string.IsNullOrWhiteSpace(token.Value);

    /// <summary>
    /// Guards that <paramref name="token"/> contains a non-whitespace value, returning
    /// the validated instance. Throws <see cref="InvalidOperationException"/> with
    /// <paramref name="context"/> in the message when validation fails.
    /// </summary>
    public static SensitiveString RequireValid(this SensitiveString? token, string context)
    {
        if (token.IsNullOrEmpty())
            throw new InvalidOperationException($"{context} is required but was not configured.");
        return token;
    }

    /// <summary>
    /// Projects a <see cref="SensitiveString"/>-valued map into raw strings — the form
    /// the SDK transport layer needs (HTTP <c>AdditionalHeaders</c> and similar).
    /// Returns <see langword="null"/> for null or empty input so the caller can pass
    /// the result straight to a transport option without an extra branch. Necessary
    /// because <see cref="SensitiveString.ToString"/> returns the redacted sentinel,
    /// so a casual <c>new Dictionary&lt;string,string&gt;(map)</c> would compile but
    /// silently ship "***REDACTED***" over the wire.
    /// </summary>
    public static Dictionary<string, string>? ToRawValues(
        this IDictionary<string, SensitiveString>? source,
        StringComparer? comparer = null)
    {
        if (source is not { Count: > 0 })
            return null;
        return source.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value, comparer ?? StringComparer.Ordinal);
    }

    /// <summary>
    /// Same as <see cref="ToRawValues"/> but with <c>string?</c> values — what
    /// <see cref="ModelContextProtocol.Client.StdioClientTransportOptions.EnvironmentVariables"/>
    /// requires. Keep this overload distinct: there is no useful covariance from
    /// <c>Dictionary&lt;string,string&gt;</c> to a nullable-value dictionary.
    /// </summary>
    public static Dictionary<string, string?>? ToRawNullableValues(
        this IDictionary<string, SensitiveString>? source,
        StringComparer? comparer = null)
    {
        if (source is not { Count: > 0 })
            return null;
        return source.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value.Value, comparer ?? StringComparer.Ordinal);
    }
}

/// <summary>
/// Enables Microsoft.Extensions.Configuration to bind JSON string values
/// directly to <see cref="SensitiveString"/> properties.
/// When <see cref="Protector"/> is set, transparently decrypts <c>ENC:</c> values.
/// </summary>
public sealed class SensitiveStringTypeConverter : TypeConverter
{
    /// <summary>
    /// Set at startup before config binding begins. Enables transparent decryption
    /// of <c>ENC:</c> prefixed values. TypeConverters don't support DI, so this
    /// uses a static accessor initialized during host setup.
    /// </summary>
    public static ISecretsProtector? Protector { get; set; }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value)
    {
        if (value is string s)
        {
            if (Protector is not null && ISecretsProtector.IsEncrypted(s))
                s = Protector.Unprotect(s);

            return new SensitiveString(s);
        }

        return base.ConvertFrom(context, culture, value);
    }
}

/// <summary>
/// System.Text.Json converter for <see cref="SensitiveString"/>. Used by code paths
/// that deserialize via STJ (for example, the MCP OAuth credential store)
/// rather than M.E.Configuration binding.
/// </summary>
public sealed class SensitiveStringJsonConverter : JsonConverter<SensitiveString>
{
    public override SensitiveString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s is null)
            return null;

        if (SensitiveStringTypeConverter.Protector is { } protector && ISecretsProtector.IsEncrypted(s))
            s = protector.Unprotect(s);

        return new SensitiveString(s);
    }

    public override void Write(Utf8JsonWriter writer, SensitiveString value, JsonSerializerOptions options)
    {
        // Write the raw value — encryption happens at the SecretsFileWriter level, not here.
        writer.WriteStringValue(value.Value);
    }
}
