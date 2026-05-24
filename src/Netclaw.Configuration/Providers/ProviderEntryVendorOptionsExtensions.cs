// -----------------------------------------------------------------------
// <copyright file="ProviderEntryVendorOptionsExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Configuration.Providers;

/// <summary>
/// Deserializes the opaque vendor-options bag into a provider-owned typed view.
/// </summary>
public static class ProviderEntryVendorOptionsExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static T? GetVendorOptions<T>(this ProviderEntry entry)
        where T : class, IVendorOptions
    {
        if (entry.VendorOptions is null)
            return null;

        try
        {
            return entry.VendorOptions.Deserialize<T>(SerializerOptions)
                ?? throw new InvalidOperationException(
                    $"Providers:<name>:VendorOptions could not be bound as {typeof(T).Name}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Providers:<name>:VendorOptions is invalid for provider type '{entry.Type}' and options type '{typeof(T).Name}'.",
                ex);
        }
    }
}
