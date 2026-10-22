// -----------------------------------------------------------------------
// <copyright file="LocalControlPairingProofProtector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Netclaw.Configuration.Secrets;

/// <summary>
/// Protects the fixed local-control payload shared by the host CLI and daemon.
/// Possession of a valid proof shows access to the Netclaw host key ring.
/// </summary>
public sealed class LocalControlPairingProofProtector
{
    internal const string Purpose = "Netclaw.LocalControl.Pairing.v1";
    internal const string ProviderOAuthResultPurpose = "Netclaw.LocalControl.ProviderOAuthResult.v1";
    internal const byte CurrentVersion = 1;
    internal const byte GeneratePairingCodeOperation = 1;
    internal const byte ReadProviderOAuthResultOperation = 2;
    internal const int NonceSize = 16;
    internal const int PayloadSize = 1 + 1 + sizeof(long) + NonceSize;

    private readonly IDataProtectionProvider _provider;

    public LocalControlPairingProofProtector(IDataProtectionProvider provider)
    {
        _provider = provider;
    }

    internal string CreateProof(LocalControlOperation operation, DateTimeOffset issuedAt)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        return ProtectPayload(operation, new LocalControlPairingProofPayload(
            CurrentVersion,
            operation.Code,
            issuedAt,
            Convert.ToHexString(nonce)));
    }

    internal string ProtectPayload(LocalControlOperation operation, LocalControlPairingProofPayload payload)
    {
        byte[] nonce;
        try
        {
            nonce = Convert.FromHexString(payload.Nonce);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The local-control nonce must use hexadecimal text.", nameof(payload), ex);
        }

        if (nonce.Length != NonceSize)
            throw new ArgumentException($"The local-control nonce must contain {NonceSize} bytes.", nameof(payload));

        Span<byte> plaintext = stackalloc byte[PayloadSize];
        plaintext[0] = payload.Version;
        plaintext[1] = payload.Operation;
        BinaryPrimitives.WriteInt64BigEndian(plaintext[2..], payload.IssuedAt.ToUnixTimeMilliseconds());
        nonce.CopyTo(plaintext[10..]);

        var protectedBytes = ProtectorFor(operation).Protect(plaintext.ToArray());
        return Base64Url.EncodeToString(protectedBytes);
    }

    internal LocalControlPairingProofPayload Unprotect(LocalControlOperation operation, string proof)
    {
        if (string.IsNullOrWhiteSpace(proof))
            throw new CryptographicException("The local-control proof is empty.");

        var protectedBytes = new byte[Base64Url.GetMaxDecodedLength(proof.Length)];
        int bytesWritten;
        try
        {
            if (!Base64Url.TryDecodeFromChars(proof, protectedBytes, out bytesWritten))
                throw new CryptographicException("The local-control proof is not valid base64url data.");
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The local-control proof is not valid base64url data.", ex);
        }

        var plaintext = ProtectorFor(operation).Unprotect(protectedBytes.AsSpan(0, bytesWritten).ToArray());
        if (plaintext.Length != PayloadSize)
            throw new CryptographicException("The local-control proof payload has an invalid size.");

        DateTimeOffset issuedAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                BinaryPrimitives.ReadInt64BigEndian(plaintext.AsSpan(2, sizeof(long))));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new CryptographicException("The local-control proof issue time is invalid.", ex);
        }

        return new LocalControlPairingProofPayload(
            plaintext[0],
            plaintext[1],
            issuedAt,
            Convert.ToHexString(plaintext.AsSpan(10, NonceSize)));
    }

    // The purpose chain binds the proof to its operation and to the bound value of that
    // operation. A proof made for another operation or another OAuth state fails
    // authentication, so it cannot be replayed across operations.
    private IDataProtector ProtectorFor(LocalControlOperation operation)
        => _provider.CreateProtector(operation.Purpose, operation.SubPurposes);
}

/// <summary>
/// One daemon-host operation that a local-control proof grants.
/// </summary>
internal sealed class LocalControlOperation
{
    private LocalControlOperation(byte code, string purpose, string[] subPurposes)
    {
        Code = code;
        Purpose = purpose;
        SubPurposes = subPurposes;
    }

    internal static LocalControlOperation GeneratePairingCode { get; } = new(
        LocalControlPairingProofProtector.GeneratePairingCodeOperation,
        LocalControlPairingProofProtector.Purpose,
        []);

    internal byte Code { get; }

    internal string Purpose { get; }

    internal string[] SubPurposes { get; }

    internal static LocalControlOperation ReadProviderOAuthResult(string state)
    {
        if (string.IsNullOrEmpty(state))
            throw new ArgumentException("A provider OAuth result proof requires the OAuth state.", nameof(state));

        return new LocalControlOperation(
            LocalControlPairingProofProtector.ReadProviderOAuthResultOperation,
            LocalControlPairingProofProtector.ProviderOAuthResultPurpose,
            [state]);
    }
}

internal readonly record struct LocalControlPairingProofPayload(
    byte Version,
    byte Operation,
    DateTimeOffset IssuedAt,
    string Nonce);
