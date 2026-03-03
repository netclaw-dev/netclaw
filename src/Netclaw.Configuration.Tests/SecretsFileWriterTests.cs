using System.Runtime.InteropServices;
using Netclaw.Configuration.Secrets;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SecretsFileWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _secretsPath;

    public SecretsFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _secretsPath = Path.Combine(_tempDir, "secrets.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Write_creates_file_with_correct_content()
    {
        var json = """{"key": "value"}""";
        SecretsFileWriter.Write(_secretsPath, json);

        Assert.True(File.Exists(_secretsPath));
        Assert.Contains("key", File.ReadAllText(_secretsPath));
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
        var nestedPath = Path.Combine(_tempDir, "nested", "deep", "secrets.json");
        SecretsFileWriter.Write(nestedPath, """{}""");

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Write_with_protector_encrypts_leaf_values()
    {
        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);

        var json = """{"ApiKey": "sk-secret123", "Nested": {"Token": "tok-abc"}}""";
        SecretsFileWriter.Write(_secretsPath, json, protector);

        var result = File.ReadAllText(_secretsPath);
        Assert.DoesNotContain("sk-secret123", result);
        Assert.DoesNotContain("tok-abc", result);
        Assert.Contains("ENC:", result);
    }

    [Fact]
    public void Write_with_protector_does_not_double_encrypt()
    {
        var paths = new NetclawPaths(_tempDir);
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
