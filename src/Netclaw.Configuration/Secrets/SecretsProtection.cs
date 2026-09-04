// -----------------------------------------------------------------------
// <copyright file="SecretsProtection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.DataProtection;
using System.Runtime.InteropServices;

namespace Netclaw.Configuration.Secrets;

/// <summary>
/// Factory for creating the key-ring-bound <see cref="ISecretsProtector"/> backed by
/// ASP.NET Data Protection. Keys are stored in <c>~/.netclaw/keys/</c>, separate
/// from <c>secrets.json</c> — copying the secrets file alone is insufficient to
/// decrypt its values.
/// </summary>
public static class SecretsProtection
{
    internal const string ApplicationName = "Netclaw";

    internal static IDataProtectionProvider CreateDataProtectionProvider(NetclawPaths paths)
    {
        Directory.CreateDirectory(paths.KeysDirectory);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(
                paths.KeysDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return DataProtectionProvider.Create(
            new DirectoryInfo(paths.KeysDirectory),
            config => config.SetApplicationName(ApplicationName));
    }

    /// <summary>
    /// Create a <see cref="DataProtectionSecretsProtector"/> with keys persisted
    /// to the Netclaw keys directory.
    /// </summary>
    public static DataProtectionSecretsProtector CreateProtector(NetclawPaths paths)
    {
        return new DataProtectionSecretsProtector(CreateDataProtectionProvider(paths));
    }
}
