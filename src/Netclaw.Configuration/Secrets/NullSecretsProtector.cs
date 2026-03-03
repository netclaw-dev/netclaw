namespace Netclaw.Configuration.Secrets;

/// <summary>
/// Pass-through protector for tests — values are returned unmodified.
/// </summary>
public sealed class NullSecretsProtector : ISecretsProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string ciphertext) => ciphertext;
}
