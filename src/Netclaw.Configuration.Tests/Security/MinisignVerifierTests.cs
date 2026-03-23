using Netclaw.Configuration.Security;
using Xunit;

namespace Netclaw.Configuration.Tests.Security;

public class MinisignVerifierTests
{
    // Test fixtures generated with NSec.Cryptography Ed25519 in minisign format.
    // Key ID: 0102030405060708 (arbitrary test value)
    private const string TestPublicKeyFile =
        "untrusted comment: test key\n" +
        "RWQBAgMEBQYHCE/ac1bsM6dMe4VmOpz4nsZ11O0gFdaINQw+wtqcqA7N\n";

    private const string TestSignatureFile =
        "untrusted comment: test signature\n" +
        "RUQBAgMEBQYHCPkmuRAEYY6jkXIlXdUL0ECD0kJYh/IPELV1FUk9Jg3uMw4adHuycYOL9VAAUmY1exKfKMqyfhGgzjjZ9ejhZgg=\n" +
        "trusted comment: test\n" +
        "dGVzdA==\n";

    // Signed content (must match exactly — no trailing newline)
    private const string TestManifestContent = """{"schemaVersion":1,"latest":"1.0.0"}""";

    [Fact]
    public void ParsePublicKey_ValidKey_ReturnsComponents()
    {
        var parsed = MinisignVerifier.ParsePublicKey(TestPublicKeyFile);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Algorithm.Length);
        Assert.Equal(8, parsed.KeyId.Length);
        Assert.Equal(32, parsed.Key.Length);
        // "Ed" = 0x45, 0x64 (Ed25519 public key)
        Assert.Equal(0x45, parsed.Algorithm[0]);
        Assert.Equal(0x64, parsed.Algorithm[1]);
    }

    [Fact]
    public void ParseSignature_ValidSignature_ReturnsComponents()
    {
        var parsed = MinisignVerifier.ParseSignature(TestSignatureFile);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Algorithm.Length);
        Assert.Equal(8, parsed.KeyId.Length);
        Assert.Equal(64, parsed.Signature.Length);
        // "ED" = Ed25519 standard (non-prehashed) signature
        Assert.Equal(0x45, parsed.Algorithm[0]);
        Assert.Equal(0x44, parsed.Algorithm[1]);
    }

    [Fact]
    public void ParseSignature_MalformedBase64_ReturnsNull()
    {
        const string malformed = "untrusted comment: test\n!!!not-base64!!!\n";
        Assert.Null(MinisignVerifier.ParseSignature(malformed));
    }

    [Fact]
    public void ParseSignature_WrongBlobLength_ReturnsNull()
    {
        // base64 of 10 bytes (too short for 74-byte signature blob)
        const string tooShort = "untrusted comment: test\nAAECAwQFBgcICQ==\n";
        Assert.Null(MinisignVerifier.ParseSignature(tooShort));
    }

    [Fact]
    public void ParseSignature_MissingUntrustedComment_ReturnsNull()
    {
        const string noComment = "some other header\nRUQBAgMEBQYHCPkmuRAEYY6jkXIlXdUL0ECD0kJYh/IPELV1FUk9Jg3uMw4adHuycYOL9VAAUmY1exKfKMqyfhGgzjjZ9ejhZgg=\n";
        Assert.Null(MinisignVerifier.ParseSignature(noComment));
    }

    [Fact]
    public void ParseSignature_EmptyString_ReturnsNull()
    {
        Assert.Null(MinisignVerifier.ParseSignature(""));
    }

    [Fact]
    public void VerifyEd25519_ValidSignature_ReturnsTrue()
    {
        var pubKey = MinisignVerifier.ParsePublicKey(TestPublicKeyFile)!;
        var sig = MinisignVerifier.ParseSignature(TestSignatureFile)!;
        var data = System.Text.Encoding.UTF8.GetBytes(TestManifestContent);

        var result = MinisignVerifier.VerifyEd25519(pubKey.Key, data, sig.Signature);

        Assert.True(result);
    }

    [Fact]
    public void VerifyEd25519_TamperedData_ReturnsFalse()
    {
        var pubKey = MinisignVerifier.ParsePublicKey(TestPublicKeyFile)!;
        var sig = MinisignVerifier.ParseSignature(TestSignatureFile)!;
        var data = System.Text.Encoding.UTF8.GetBytes(TestManifestContent + "TAMPERED");

        var result = MinisignVerifier.VerifyEd25519(pubKey.Key, data, sig.Signature);

        Assert.False(result);
    }

    [Fact]
    public void Verify_WithEmbeddedKey_ReturnsKeyMismatch_ForTestFixture()
    {
        // The test fixture was signed with a different key than the embedded production key,
        // so verification with the embedded key should return KeyMismatch
        var data = System.Text.Encoding.UTF8.GetBytes(TestManifestContent);

        var result = MinisignVerifier.Verify(data, TestSignatureFile);

        Assert.Equal(MinisignVerifier.VerifyResult.KeyMismatch, result);
    }

    [Fact]
    public void Verify_MalformedSignature_ReturnsMalformedSignature()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("test");

        var result = MinisignVerifier.Verify(data, "garbage\ndata\n");

        Assert.Equal(MinisignVerifier.VerifyResult.MalformedSignature, result);
    }

    [Fact]
    public void EmbeddedPublicKey_HasCorrectLength()
    {
        Assert.Equal(32, MinisignVerifier.EmbeddedPublicKey.Length);
    }

    [Fact]
    public void EmbeddedKeyId_HasCorrectLength()
    {
        Assert.Equal(8, MinisignVerifier.EmbeddedKeyId.Length);
    }

    [Fact]
    public void ParsePublicKey_MatchesEmbeddedKey_ForProductionKeyFile()
    {
        // Verify the embedded key matches what ParsePublicKey would extract
        // from the production manifest.pub file
        const string productionPubKeyFile =
            "untrusted comment: minisign public key C29536096818F582\n" +
            "RWSC9RhoCTaVwiEndieBGwzLy8wWUSzm3p0FsgOEWhEHv95/B5R9WOTD\n";

        var parsed = MinisignVerifier.ParsePublicKey(productionPubKeyFile);

        Assert.NotNull(parsed);
        Assert.True(parsed.Key.AsSpan().SequenceEqual(MinisignVerifier.EmbeddedPublicKey));
        Assert.True(parsed.KeyId.AsSpan().SequenceEqual(MinisignVerifier.EmbeddedKeyId));
    }
}
