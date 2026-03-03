using Microsoft.AspNetCore.DataProtection;

namespace Netclaw.Configuration.Secrets;

/// <summary>
/// Machine-bound secrets encryption using ASP.NET Data Protection API.
/// Values are prefixed with <c>ENC:</c> to distinguish encrypted from plaintext.
/// Key material is stored separately in <c>~/.netclaw/keys/</c>.
/// </summary>
public sealed class DataProtectionSecretsProtector : ISecretsProtector
{
    internal const string Purpose = "Netclaw.Secrets.v1";
    private const string EncryptedPrefix = "ENC:";

    private readonly IDataProtector _protector;

    public DataProtectionSecretsProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext)
    {
        var encrypted = _protector.Protect(plaintext);
        return EncryptedPrefix + encrypted;
    }

    public string Unprotect(string ciphertext)
    {
        if (!ciphertext.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Value does not have the ENC: prefix.", nameof(ciphertext));

        var payload = ciphertext[EncryptedPrefix.Length..];
        return _protector.Unprotect(payload);
    }
}
