using System.Buffers.Text;
using NSec.Cryptography;

namespace Netclaw.Configuration.Security;

/// <summary>
/// Verifies minisign Ed25519 signatures on binary feed manifests.
/// Minisign format: https://jedisct1.github.io/minisign/
/// </summary>
public static class MinisignVerifier
{
    // Minisign algorithm identifiers for Ed25519:
    // "ED" = standard (non-prehashed) detached signature — used by `minisign -S`
    // "Ed" = prehashed mode — used by `minisign -SH`
    // We accept both since the verification math is identical for Ed25519 pure.
    private static ReadOnlySpan<byte> Ed25519Standard => "ED"u8;
    private static ReadOnlySpan<byte> Ed25519Prehashed => "Ed"u8;

    // Embedded public key from feeds/releases/manifest.pub
    // Decoded from: RWSC9RhoCTaVwiEndieBGwzLy8wWUSzm3p0FsgOEWhEHv95/B5R9WOTD
    // Format: 2-byte algorithm ("Ed") + 8-byte key ID + 32-byte Ed25519 public key
    private static ReadOnlySpan<byte> EmbeddedPublicKeyBlob => [
        0x45, 0x64, // "Ed" algorithm
        0x82, 0xf5, 0x18, 0x68, 0x09, 0x36, 0x95, 0xc2, // Key ID: C29536096818F582 (little-endian in file, display is big-endian)
        0x21, 0x27, 0x76, 0x27, 0x81, 0x1b, 0x0c, 0xcb, // Ed25519 public key (32 bytes)
        0xcb, 0xcc, 0x16, 0x51, 0x2c, 0xe6, 0xde, 0x9d,
        0x05, 0xb2, 0x03, 0x84, 0x5a, 0x11, 0x07, 0xbf,
        0xde, 0x7f, 0x07, 0x94, 0x7d, 0x58, 0xe4, 0xc3,
    ];

    /// <summary>
    /// Test seam: when set, overrides the embedded public key for verification.
    /// Must be a 42-byte minisign public key blob (2 algo + 8 key ID + 32 key).
    /// Reset to null after tests to restore production behavior.
    /// </summary>
    internal static byte[]? TestPublicKeyOverride { get; set; }

    /// <summary>
    /// The embedded Ed25519 public key ID (8 bytes) for matching against signatures.
    /// </summary>
    internal static ReadOnlySpan<byte> EmbeddedKeyId => EmbeddedPublicKeyBlob.Slice(2, 8);

    /// <summary>
    /// The embedded Ed25519 public key (32 bytes) for signature verification.
    /// </summary>
    internal static ReadOnlySpan<byte> EmbeddedPublicKey => EmbeddedPublicKeyBlob.Slice(10, 32);

    /// <summary>
    /// Result of a signature verification attempt.
    /// </summary>
    public enum VerifyResult
    {
        /// <summary>Signature is valid.</summary>
        Valid,

        /// <summary>Signature file format is malformed.</summary>
        MalformedSignature,

        /// <summary>Signature uses an unsupported algorithm (not Ed25519 pure).</summary>
        UnsupportedAlgorithm,

        /// <summary>Signature was produced by a different key than the embedded public key.</summary>
        KeyMismatch,

        /// <summary>Signature does not match the provided data.</summary>
        InvalidSignature,
    }

    /// <summary>
    /// Parses a minisign signature file into its components.
    /// Format: line 1 = "untrusted comment: ...", line 2 = base64(2-byte algo + 8-byte key ID + 64-byte signature).
    /// </summary>
    /// <returns>The parsed signature, or null if the format is invalid.</returns>
    internal static ParsedSignature? ParseSignature(string signatureFileContent)
    {
        var lines = signatureFileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return null;

        // Line 1 must start with "untrusted comment:"
        if (!lines[0].StartsWith("untrusted comment:", StringComparison.Ordinal))
            return null;

        // Line 2 is base64-encoded signature blob
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(lines[1].Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        // Expected: 2 (algo) + 8 (key ID) + 64 (Ed25519 signature) = 74 bytes
        if (blob.Length != 74)
            return null;

        return new ParsedSignature
        {
            Algorithm = blob.AsSpan(0, 2).ToArray(),
            KeyId = blob.AsSpan(2, 8).ToArray(),
            Signature = blob.AsSpan(10, 64).ToArray(),
        };
    }

    /// <summary>
    /// Parses a minisign public key file into its components.
    /// Format: line 1 = "untrusted comment: ...", line 2 = base64(2-byte algo + 8-byte key ID + 32-byte key).
    /// </summary>
    internal static ParsedPublicKey? ParsePublicKey(string publicKeyFileContent)
    {
        var lines = publicKeyFileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(lines[1].Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        // Expected: 2 (algo) + 8 (key ID) + 32 (Ed25519 public key) = 42 bytes
        if (blob.Length != 42)
            return null;

        return new ParsedPublicKey
        {
            Algorithm = blob.AsSpan(0, 2).ToArray(),
            KeyId = blob.AsSpan(2, 8).ToArray(),
            Key = blob.AsSpan(10, 32).ToArray(),
        };
    }

    /// <summary>
    /// Verifies a minisign signature against data using the embedded public key.
    /// </summary>
    /// <param name="data">The signed data (manifest content).</param>
    /// <param name="signatureFileContent">The raw content of the .sig file.</param>
    /// <returns>The verification result.</returns>
    public static VerifyResult Verify(ReadOnlySpan<byte> data, string signatureFileContent)
    {
        var sig = ParseSignature(signatureFileContent);
        if (sig is null)
            return VerifyResult.MalformedSignature;

        // Check algorithm is Ed25519 (standard or prehashed)
        if (!sig.Algorithm.AsSpan().SequenceEqual(Ed25519Standard) &&
            !sig.Algorithm.AsSpan().SequenceEqual(Ed25519Prehashed))
            return VerifyResult.UnsupportedAlgorithm;

        // Use test override if set, otherwise embedded production key
        ReadOnlySpan<byte> activeKeyId;
        ReadOnlySpan<byte> activePublicKey;
        if (TestPublicKeyOverride is { Length: 42 } testBlob)
        {
            activeKeyId = testBlob.AsSpan(2, 8);
            activePublicKey = testBlob.AsSpan(10, 32);
        }
        else
        {
            activeKeyId = EmbeddedKeyId;
            activePublicKey = EmbeddedPublicKey;
        }

        // Check key ID matches active key
        if (!sig.KeyId.AsSpan().SequenceEqual(activeKeyId))
            return VerifyResult.KeyMismatch;

        return VerifyEd25519(activePublicKey, data, sig.Signature)
            ? VerifyResult.Valid
            : VerifyResult.InvalidSignature;
    }

    /// <summary>
    /// Verifies an Ed25519 signature using NSec.Cryptography.
    /// </summary>
    internal static bool VerifyEd25519(
        ReadOnlySpan<byte> publicKeyBytes,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signatureBytes)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var publicKey = PublicKey.Import(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);
        return algorithm.Verify(publicKey, data, signatureBytes);
    }

    internal sealed class ParsedSignature
    {
        public required byte[] Algorithm { get; init; }
        public required byte[] KeyId { get; init; }
        public required byte[] Signature { get; init; }
    }

    internal sealed class ParsedPublicKey
    {
        public required byte[] Algorithm { get; init; }
        public required byte[] KeyId { get; init; }
        public required byte[] Key { get; init; }
    }
}
