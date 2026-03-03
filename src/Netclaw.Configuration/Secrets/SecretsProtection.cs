using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Configuration.Secrets;

/// <summary>
/// Factory for creating the machine-bound <see cref="ISecretsProtector"/> backed by
/// ASP.NET Data Protection. Keys are stored in <c>~/.netclaw/keys/</c>, separate
/// from <c>secrets.json</c> — copying the secrets file alone is insufficient to
/// decrypt its values.
/// </summary>
public static class SecretsProtection
{
    /// <summary>
    /// Create a <see cref="DataProtectionSecretsProtector"/> with keys persisted
    /// to the Netclaw keys directory.
    /// </summary>
    public static DataProtectionSecretsProtector CreateProtector(NetclawPaths paths)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Netclaw")
            .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDirectory));

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IDataProtectionProvider>();
        return new DataProtectionSecretsProtector(provider);
    }
}
