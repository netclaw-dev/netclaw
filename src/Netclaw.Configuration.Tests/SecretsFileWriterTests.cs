// -----------------------------------------------------------------------
// <copyright file="SecretsFileWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        SecretsFileWriter.Write(_secretsPath, json, new NullSecretsProtector());

        Assert.True(File.Exists(_secretsPath));
        Assert.Contains("key", File.ReadAllText(_secretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_sets_chmod_600_on_linux()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on Windows

        SecretsFileWriter.Write(_secretsPath, """{"test": true}""", new NullSecretsProtector());

        var mode = File.GetUnixFileMode(_secretsPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Write_creates_parent_directories()
    {
        var nestedPath = Path.Combine(_dir.Path, "nested", "deep", "secrets.json");
        SecretsFileWriter.Write(nestedPath, """{}""", new NullSecretsProtector());

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

    [Fact]
    public async Task Update_serializes_cross_section_mutations_and_second_writer_observes_committed_state()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var protector = SecretsProtection.CreateProtector(paths);
        SecretsFileWriter.Write(_secretsPath,
            """
            {
              "Slack": {
                "BotToken": "xoxb-existing"
              }
            }
            """,
            protector);

        var secondSecretsPath = _secretsPath;
        string? aliasPath = null;
        if (!OperatingSystem.IsWindows())
        {
            aliasPath = $"{_dir.Path}-alias";
            Directory.CreateSymbolicLink(aliasPath, _dir.Path);
            secondSecretsPath = Path.Combine(aliasPath, "secrets.json");
        }

        using var firstEntered = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        string? secondObservedBraveKey = null;

        try
        {
            var first = Task.Run(() => SecretsFileWriter.Update<bool>(
                _secretsPath,
                (root, _) =>
                {
                    firstEntered.Set();
                    Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10), cancellationToken));

                    root["Search"] = new JsonObject
                    {
                        ["BraveApiKey"] = "brave-key"
                    };
                    return (root, true);
                },
                protector: protector,
                cancellationToken: cancellationToken), cancellationToken);

            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10), cancellationToken));

            var second = Task.Run(() =>
            {
                secondStarted.Set();
                return SecretsFileWriter.Update<bool>(
                    secondSecretsPath,
                    (root, _) =>
                    {
                        secondObservedBraveKey = root["Search"]?["BraveApiKey"]?.GetValue<string>();
                        root["McpOAuthTokens"] = new JsonObject
                        {
                            ["memorizer"] = new JsonObject
                            {
                                ["AccessToken"] = "rotated-access-token"
                            }
                        };
                        return (root, true);
                    },
                    protector: protector,
                    cancellationToken: cancellationToken);
            }, cancellationToken);

            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            releaseFirst.Set();
            await Task.WhenAll(first, second);

            Assert.Equal("brave-key", secondObservedBraveKey);

            var decrypted = SecretsFileWriter.DecryptJsonLeaves(File.ReadAllText(_secretsPath), protector);
            using var doc = JsonDocument.Parse(decrypted);
            Assert.Equal("xoxb-existing", doc.RootElement.GetProperty("Slack").GetProperty("BotToken").GetString());
            Assert.Equal("brave-key", doc.RootElement.GetProperty("Search").GetProperty("BraveApiKey").GetString());
            Assert.Equal("rotated-access-token",
                doc.RootElement.GetProperty("McpOAuthTokens").GetProperty("memorizer").GetProperty("AccessToken").GetString());
            Assert.DoesNotContain("xoxb-existing", File.ReadAllText(_secretsPath), StringComparison.Ordinal);
            Assert.DoesNotContain("rotated-access-token", File.ReadAllText(_secretsPath), StringComparison.Ordinal);
        }
        finally
        {
            releaseFirst.Set();
            if (aliasPath is not null)
                Directory.Delete(aliasPath);
        }
    }

    [Fact]
    public void Update_when_file_replacement_fails_propagates_and_keeps_previous_content()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        SecretsFileWriter.Write(_secretsPath, """{"Existing":"keep"}""", new NullSecretsProtector());
        var before = File.ReadAllText(_secretsPath);
        var originalMode = File.GetUnixFileMode(_dir.Path);

        try
        {
            File.SetUnixFileMode(_dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            Assert.ThrowsAny<Exception>(() => SecretsFileWriter.Update<bool>(
                _secretsPath,
                (root, _) =>
                {
                    root["Existing"] = "lost";
                    return (root, true);
                },
                new NullSecretsProtector(),
                cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            File.SetUnixFileMode(_dir.Path, originalMode);
        }

        Assert.Equal(before, File.ReadAllText(_secretsPath));
    }
}
