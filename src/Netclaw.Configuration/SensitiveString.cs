using System.ComponentModel;
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
/// that deserialize via STJ (e.g., <c>McpOAuthService.LoadTokensFromDisk</c>)
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
