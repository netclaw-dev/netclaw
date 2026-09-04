// -----------------------------------------------------------------------
// <copyright file="LocalControlPairingProofProtectorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class LocalControlPairingProofProtectorTests : IDisposable
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    private readonly DisposableTempDir _dir = new();

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public void Same_key_ring_round_trips_fixed_payload()
    {
        var paths = CreatePaths(_dir.Path);
        var provider = SecretsProtection.CreateDataProtectionProvider(paths);
        var writer = new LocalControlPairingProofProtector(provider);
        var reader = new LocalControlPairingProofProtector(
            SecretsProtection.CreateDataProtectionProvider(paths));

        var proof = writer.ProtectPayload(new LocalControlPairingProofPayload(
            LocalControlPairingProofProtector.CurrentVersion,
            LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            IssuedAt,
            "00112233445566778899AABBCCDDEEFF"));

        var payload = reader.Unprotect(proof);

        Assert.Equal(LocalControlPairingProofProtector.CurrentVersion, payload.Version);
        Assert.Equal(LocalControlPairingProofProtector.GeneratePairingCodeOperation, payload.Operation);
        Assert.Equal(IssuedAt, payload.IssuedAt);
        Assert.Equal("00112233445566778899AABBCCDDEEFF", payload.Nonce);
    }

    [Fact]
    public void CreateProof_uses_a_fresh_128_bit_nonce()
    {
        var paths = CreatePaths(_dir.Path);
        var protector = new LocalControlPairingProofProtector(
            SecretsProtection.CreateDataProtectionProvider(paths));

        var first = protector.Unprotect(protector.CreateProof(IssuedAt));
        var second = protector.Unprotect(protector.CreateProof(IssuedAt));

        Assert.Equal(LocalControlPairingProofProtector.NonceSize * 2, first.Nonce.Length);
        Assert.NotEqual(first.Nonce, second.Nonce);
    }

    [Fact]
    public void Different_key_ring_cannot_read_proof()
    {
        using var otherDir = new DisposableTempDir();
        var writer = new LocalControlPairingProofProtector(
            SecretsProtection.CreateDataProtectionProvider(CreatePaths(_dir.Path)));
        var reader = new LocalControlPairingProofProtector(
            SecretsProtection.CreateDataProtectionProvider(CreatePaths(otherDir.Path)));

        var proof = writer.CreateProof(IssuedAt);

        Assert.Throws<CryptographicException>(() => reader.Unprotect(proof));
    }

    [Fact]
    public void Secrets_purpose_cannot_read_local_control_payload()
    {
        var paths = CreatePaths(_dir.Path);
        var provider = SecretsProtection.CreateDataProtectionProvider(paths);
        var localControl = new LocalControlPairingProofProtector(provider);
        var secretsProtector = provider.CreateProtector(DataProtectionSecretsProtector.Purpose);
        var proof = localControl.CreateProof(IssuedAt);
        var protectedBytes = new byte[Base64Url.GetMaxDecodedLength(proof.Length)];
        Assert.True(Base64Url.TryDecodeFromChars(proof, protectedBytes, out var bytesWritten));

        Assert.Throws<CryptographicException>(() =>
            secretsProtector.Unprotect(protectedBytes.AsSpan(0, bytesWritten).ToArray()));
    }

    [Fact]
    public void Malformed_proof_fails_closed()
    {
        var paths = CreatePaths(_dir.Path);
        var protector = new LocalControlPairingProofProtector(
            SecretsProtection.CreateDataProtectionProvider(paths));

        Assert.Throws<CryptographicException>(() => protector.Unprotect("not a proof"));
    }

    [Fact]
    public void Key_path_that_is_a_file_fails_clearly()
    {
        var basePath = Path.Combine(_dir.Path, "blocked-home");
        Directory.CreateDirectory(basePath);
        File.WriteAllText(Path.Combine(basePath, "keys"), "blocked");
        var paths = new NetclawPaths(basePath);

        Assert.ThrowsAny<IOException>(() => SecretsProtection.CreateDataProtectionProvider(paths));
    }

    [Fact]
    public void Unix_key_directory_is_owner_only()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var paths = CreatePaths(_dir.Path);
        File.SetUnixFileMode(
            paths.KeysDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        _ = SecretsProtection.CreateDataProtectionProvider(paths);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(paths.KeysDirectory));
    }

    [Fact]
    public void First_use_creates_an_owner_only_key_directory_on_Unix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var paths = new NetclawPaths(Path.Combine(_dir.Path, "first-use"));
        Assert.False(Directory.Exists(paths.KeysDirectory));

        _ = SecretsProtection.CreateDataProtectionProvider(paths);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(paths.KeysDirectory));
    }

    [Fact]
    public void Corrupt_key_file_fails_before_proof_creation()
    {
        var paths = CreatePaths(_dir.Path);
        File.WriteAllText(Path.Combine(paths.KeysDirectory, $"key-{Guid.NewGuid():D}.xml"), "<key>");
        var protector = new LocalControlPairingProofProtector(
            SecretsProtection.CreateDataProtectionProvider(paths));

        Assert.Throws<CryptographicException>(() => protector.CreateProof(IssuedAt));
    }

    private static NetclawPaths CreatePaths(string basePath)
    {
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }
}
