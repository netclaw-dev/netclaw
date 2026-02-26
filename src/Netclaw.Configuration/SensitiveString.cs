using System.ComponentModel;
using System.Globalization;

namespace Netclaw.Configuration;

/// <summary>
/// Wraps a secret value (API key, OAuth token) to prevent accidental
/// exposure in logs, debug output, or string interpolation.
/// Access the inner value explicitly via <see cref="Value"/>.
/// </summary>
/// <param name="Value">The secret value.</param>
[TypeConverter(typeof(SensitiveStringTypeConverter))]
public sealed record SensitiveString(string Value)
{
    public override string ToString() => "***REDACTED***";
}

/// <summary>
/// Enables Microsoft.Extensions.Configuration to bind JSON string values
/// directly to <see cref="SensitiveString"/> properties.
/// </summary>
public sealed class SensitiveStringTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value)
    {
        if (value is string s)
            return new SensitiveString(s);

        return base.ConvertFrom(context, culture, value);
    }
}
