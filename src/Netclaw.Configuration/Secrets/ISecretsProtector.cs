// -----------------------------------------------------------------------
// <copyright file="ISecretsProtector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration.Secrets;

/// <summary>
/// Encrypts and decrypts secret string values using an <c>ENC:</c> prefix convention.
/// The JSON structure stays human-readable — only leaf string values are opaque.
/// </summary>
public interface ISecretsProtector
{
    /// <summary>
    /// Encrypt a plaintext value. Returns an <c>ENC:...</c> prefixed ciphertext.
    /// </summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypt an <c>ENC:...</c> prefixed ciphertext back to plaintext.
    /// </summary>
    string Unprotect(string ciphertext);

    /// <summary>
    /// Returns true if the value starts with the <c>ENC:</c> prefix,
    /// indicating it is already encrypted.
    /// </summary>
    static bool IsEncrypted(string value) => value.StartsWith("ENC:", StringComparison.Ordinal);
}
