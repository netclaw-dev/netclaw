// -----------------------------------------------------------------------
// <copyright file="DataProtectionSecretsProtectorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class DataProtectionSecretsProtectorTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly DataProtectionSecretsProtector _protector;

    public DataProtectionSecretsProtectorTests()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _protector = SecretsProtection.CreateProtector(paths);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public void Protect_returns_ENC_prefixed_value()
    {
        var result = _protector.Protect("my-secret");
        Assert.StartsWith("ENC:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trip_preserves_value()
    {
        const string original = "sk-abc123-very-secret-key";
        var encrypted = _protector.Protect(original);
        var decrypted = _protector.Unprotect(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Unprotect_throws_for_non_ENC_value()
    {
        Assert.Throws<ArgumentException>(() => _protector.Unprotect("plaintext-value"));
    }

    [Fact]
    public void IsEncrypted_detects_ENC_prefix()
    {
        Assert.True(ISecretsProtector.IsEncrypted("ENC:CfDJ8B..."));
        Assert.False(ISecretsProtector.IsEncrypted("sk-abc123"));
        Assert.False(ISecretsProtector.IsEncrypted(""));
    }

    [Fact]
    public void Different_values_produce_different_ciphertexts()
    {
        var a = _protector.Protect("secret-a");
        var b = _protector.Protect("secret-b");

        Assert.NotEqual(a, b);
    }
}
