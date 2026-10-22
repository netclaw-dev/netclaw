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

    private const string Expiry = "2026-06-01T01:00:00.0000000+00:00";

    [Fact]
    public void Write_keeps_a_provider_token_expiry_unencrypted()
    {
        SecretsFileWriter.Write(_secretsPath, $$"""
            {
              "Providers": { "copilot": { "OAuthTokenExpiry": "{{Expiry}}", "OAuthRefreshToken": "refresh-1", "OAuthAccessToken": "access-1", "ApiKey": "sk-1", "OAuthAccountId": "acct-1" } },
              "providers": { "Copilot": { "oauthtokenexpiry": "{{Expiry}}" } },
              "Other": { "copilot": { "OAuthTokenExpiry": null } }
            }
            """, new PrefixProtector());

        var root = JsonNode.Parse(File.ReadAllText(_secretsPath))!.AsObject();
        var entry = root["Providers"]!["copilot"]!.AsObject();
        Assert.Equal(Expiry, entry["OAuthTokenExpiry"]!.GetValue<string>());
        Assert.Equal("ENC:refresh-1", entry["OAuthRefreshToken"]!.GetValue<string>());
        Assert.Equal("ENC:access-1", entry["OAuthAccessToken"]!.GetValue<string>());
        Assert.Equal("ENC:acct-1", entry["OAuthAccountId"]!.GetValue<string>());
        Assert.Equal("ENC:sk-1", entry["ApiKey"]!.GetValue<string>());
        Assert.Equal(Expiry, root["providers"]!["Copilot"]!["oauthtokenexpiry"]!.GetValue<string>());
        Assert.Null(root["Other"]!["copilot"]!["OAuthTokenExpiry"]);
    }

    [Theory]
    [InlineData("""{"McpOAuthTokens":{"srv":{"ExpiresAt":"X"}}}""", "McpOAuthTokens.srv.ExpiresAt")]
    [InlineData("""{"OAuthTokenExpiry":"X"}""", "OAuthTokenExpiry")]
    [InlineData("""{"Providers":{"OAuthTokenExpiry":"X"}}""", "Providers.OAuthTokenExpiry")]
    [InlineData("""{"Providers":{"copilot":{"VendorOptions":{"OAuthTokenExpiry":"X"}}}}""", "Providers.copilot.VendorOptions.OAuthTokenExpiry")]
    [InlineData("""{"Channels":{"copilot":{"OAuthTokenExpiry":"X"}}}""", "Channels.copilot.OAuthTokenExpiry")]
    [InlineData("""{"Providers":{"copilot":{"Tags":["OAuthTokenExpiry"]}}}""", "Providers.copilot.Tags.0")]
    [InlineData("""{"Providers:copilot:OAuthTokenExpiry":"X"}""", "Providers:copilot:OAuthTokenExpiry")]
    [InlineData("""{"Providers":{"copilot":{"OAuthRefreshToken":"X"}}}""", "Providers.copilot.OAuthRefreshToken")]
    public void Write_encrypts_every_other_leaf(string json, string leafPath)
    {
        SecretsFileWriter.Write(_secretsPath, json, new PrefixProtector());

        JsonNode? node = JsonNode.Parse(File.ReadAllText(_secretsPath));
        // A flattened root key is one segment, so it is not split.
        var segments = leafPath.Contains(':', StringComparison.Ordinal) ? new[] { leafPath } : leafPath.Split('.');
        foreach (var segment in segments)
        {
            node = node is JsonArray array
                ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]
                : node![segment];
        }

        Assert.Equal("ENC:", node!.GetValue<string>()[..4]);
    }

    [Fact]
    public void Write_leaves_an_encrypted_token_expiry_as_it_is()
    {
        SecretsFileWriter.Write(_secretsPath,
            """{"Providers":{"copilot":{"OAuthTokenExpiry":"ENC:abc"}}}""", new PrefixProtector());

        var entry = JsonNode.Parse(File.ReadAllText(_secretsPath))!["Providers"]!["copilot"]!;
        Assert.Equal("ENC:abc", entry["OAuthTokenExpiry"]!.GetValue<string>());
    }

    [Fact]
    public void Update_saves_an_encrypted_token_expiry_from_an_old_write_unencrypted()
    {
        File.WriteAllText(_secretsPath,
            """{"Providers":{"copilot":{"OAuthTokenExpiry":"ENC:2026-06-01T01:00:00.0000000+00:00"}}}""");
        string? seen = null;

        SecretsFileWriter.Update(_secretsPath, (root, _) =>
        {
            seen = root["Providers"]!["copilot"]!["OAuthTokenExpiry"]!.GetValue<string>();
            return (root, 0);
        }, new PrefixProtector(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Expiry, seen);
        var entry = JsonNode.Parse(File.ReadAllText(_secretsPath))!["Providers"]!["copilot"]!;
        Assert.Equal(Expiry, entry["OAuthTokenExpiry"]!.GetValue<string>());
    }

    [Fact]
    public void CountEncryptionStatus_does_not_count_the_token_expiry_as_plaintext()
    {
        var (encrypted, plaintext) = SecretsFileWriter.CountEncryptionStatus(
            $$$$"""{"Providers":{"copilot":{"OAuthTokenExpiry":"{{{{Expiry}}}}","OAuthRefreshToken":"ENC:x","Other":"plain"}}}""");

        Assert.Equal(1, encrypted);
        Assert.Equal(1, plaintext);
    }

    /// <summary>Marks each protected value with the real <c>ENC:</c> prefix. No key, so the tests read the plaintext back.</summary>
    private sealed class PrefixProtector : ISecretsProtector
    {
        public string Protect(string plaintext) => "ENC:" + plaintext;
        public string Unprotect(string ciphertext) => ciphertext["ENC:".Length..];
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
