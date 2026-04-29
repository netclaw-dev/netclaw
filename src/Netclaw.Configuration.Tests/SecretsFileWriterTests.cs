// -----------------------------------------------------------------------
// <copyright file="SecretsFileWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.InteropServices;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SecretsFileWriterTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _secretsPath;

    public SecretsFileWriterTests()
    {
        _secretsPath = Path.Combine(_dir.Path, "secrets.json");
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public void Write_creates_file_with_correct_content()
    {
        var json = """{"key": "value"}""";
        SecretsFileWriter.Write(_secretsPath, json);

        Assert.True(File.Exists(_secretsPath));
        Assert.Contains("key", File.ReadAllText(_secretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_sets_chmod_600_on_linux()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on Windows

        SecretsFileWriter.Write(_secretsPath, """{"test": true}""");

        var mode = File.GetUnixFileMode(_secretsPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Write_creates_parent_directories()
    {
        var nestedPath = Path.Combine(_dir.Path, "nested", "deep", "secrets.json");
        SecretsFileWriter.Write(nestedPath, """{}""");

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Write_with_protector_encrypts_leaf_values()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);

        var json = """{"ApiKey": "sk-secret123", "Nested": {"Token": "tok-abc"}}""";
        SecretsFileWriter.Write(_secretsPath, json, protector);

        var result = File.ReadAllText(_secretsPath);
        Assert.DoesNotContain("sk-secret123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-abc", result, StringComparison.Ordinal);
        Assert.Contains("ENC:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_with_protector_does_not_double_encrypt()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);

        var json = """{"ApiKey": "sk-secret123"}""";

        // Encrypt once
        SecretsFileWriter.Write(_secretsPath, json, protector);
        var firstPass = File.ReadAllText(_secretsPath);

        // Encrypt again (should be idempotent)
        SecretsFileWriter.Write(_secretsPath, firstPass, protector);
        var secondPass = File.ReadAllText(_secretsPath);

        Assert.Equal(firstPass, secondPass);
    }

    [Fact]
    public void DecryptJsonLeaves_round_trips_with_encrypt()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);

        var json = """{"ApiKey": "sk-secret123", "Nested": {"Token": "tok-abc", "Expiry": "2026-03-21"}}""";

        // Encrypt via Write
        SecretsFileWriter.Write(_secretsPath, json, protector);
        var encrypted = File.ReadAllText(_secretsPath);
        Assert.Contains("ENC:", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret123", encrypted, StringComparison.Ordinal);

        // Decrypt
        var decrypted = SecretsFileWriter.DecryptJsonLeaves(encrypted, protector);
        Assert.Contains("sk-secret123", decrypted, StringComparison.Ordinal);
        Assert.Contains("tok-abc", decrypted, StringComparison.Ordinal);
        Assert.Contains("2026-03-21", decrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("ENC:", decrypted, StringComparison.Ordinal);
    }

    [Fact]
    public void DecryptJsonLeaves_leaves_plaintext_untouched()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);

        var json = """{"plain": "hello", "nested": {"also_plain": "world"}}""";
        var result = SecretsFileWriter.DecryptJsonLeaves(json, protector);

        Assert.Contains("hello", result, StringComparison.Ordinal);
        Assert.Contains("world", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CountEncryptionStatus_counts_correctly()
    {
        var json = """{"plain": "hello", "encrypted": "ENC:abc123", "nested": {"also_plain": "world"}}""";
        var (encrypted, plaintext) = SecretsFileWriter.CountEncryptionStatus(json);

        Assert.Equal(1, encrypted);
        Assert.Equal(2, plaintext);
    }

    [Fact]
    public void CountEncryptionStatus_empty_json_returns_zero()
    {
        var (encrypted, plaintext) = SecretsFileWriter.CountEncryptionStatus("{}");
        Assert.Equal(0, encrypted);
        Assert.Equal(0, plaintext);
    }
}
