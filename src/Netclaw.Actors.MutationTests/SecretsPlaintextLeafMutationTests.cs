// -----------------------------------------------------------------------
// <copyright file="SecretsPlaintextLeafMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.MutationTests;

// Guards the one secrets.json leaf that stays unencrypted (SecretsFileWriter.IsPlaintextLeaf).
// A mutant that encrypts the date stops the daemon at startup. A mutant that skips another leaf
// leaves a credential unencrypted at rest.
public sealed class SecretsPlaintextLeafMutationTests : IDisposable
{
    private const string Expiry = "2026-06-01T01:00:00.0000000+00:00";

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("""{"Providers":{"copilot":{"OAuthTokenExpiry":"2026-06-01T01:00:00.0000000+00:00"}}}""", "Providers.copilot.OAuthTokenExpiry")]
    [InlineData("""{"providers":{"Copilot":{"oauthtokenexpiry":"2026-06-01T01:00:00.0000000+00:00"}}}""", "providers.Copilot.oauthtokenexpiry")]
    [InlineData("""{"PROVIDERS":{"x":{"OAUTHTOKENEXPIRY":"2026-06-01T01:00:00.0000000+00:00"}}}""", "PROVIDERS.x.OAUTHTOKENEXPIRY")]
    public void The_provider_token_expiry_stays_plaintext(string json, string leafPath)
    {
        Assert.Equal(Expiry, LeafAfterWrite(json, leafPath));
    }

    [Fact]
    public void A_null_expiry_stays_null()
    {
        var root = Write("""{"Providers":{"copilot":{"OAuthTokenExpiry":null}}}""");

        Assert.Null(root["Providers"]!["copilot"]!["OAuthTokenExpiry"]);
    }

    [Theory]
    [InlineData("""{"Providers":{"copilot":{"OAuthRefreshToken":"x"}}}""", "Providers.copilot.OAuthRefreshToken")]
    [InlineData("""{"Providers":{"copilot":{"OAuthAccessToken":"x"}}}""", "Providers.copilot.OAuthAccessToken")]
    [InlineData("""{"Providers":{"copilot":{"OAuthAccountId":"x"}}}""", "Providers.copilot.OAuthAccountId")]
    [InlineData("""{"Providers":{"copilot":{"ApiKey":"x"}}}""", "Providers.copilot.ApiKey")]
    [InlineData("""{"McpOAuthTokens":{"srv":{"ExpiresAt":"x"}}}""", "McpOAuthTokens.srv.ExpiresAt")]
    [InlineData("""{"McpOAuthTokens":{"srv":{"OAuthTokenExpiry":"x"}}}""", "McpOAuthTokens.srv.OAuthTokenExpiry")]
    [InlineData("""{"OAuthTokenExpiry":"x"}""", "OAuthTokenExpiry")]
    [InlineData("""{"Providers":{"OAuthTokenExpiry":"x"}}""", "Providers.OAuthTokenExpiry")]
    [InlineData("""{"Providers":{"copilot":{"VendorOptions":{"OAuthTokenExpiry":"x"}}}}""", "Providers.copilot.VendorOptions.OAuthTokenExpiry")]
    [InlineData("""{"Channels":{"copilot":{"OAuthTokenExpiry":"x"}}}""", "Channels.copilot.OAuthTokenExpiry")]
    [InlineData("""{"Providers":{"copilot":{"Tags":["OAuthTokenExpiry"]}}}""", "Providers.copilot.Tags.0")]
    [InlineData("""{"Providers":{"copilot":{"Tags":[["x"]]}}}""", "Providers.copilot.Tags.0.0")]
    public void Every_other_leaf_is_encrypted(string json, string leafPath)
    {
        Assert.StartsWith("ENC:", LeafAfterWrite(json, leafPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_flattened_root_key_is_one_segment_and_is_encrypted()
    {
        var root = Write("""{"Providers:copilot:OAuthTokenExpiry":"x"}""");

        Assert.StartsWith("ENC:", root["Providers:copilot:OAuthTokenExpiry"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_encrypted_value_is_not_encrypted_again_and_an_encrypted_expiry_is_not_decrypted()
    {
        var root = Write("""{"Providers":{"copilot":{"OAuthRefreshToken":"ENC:abc","OAuthTokenExpiry":"ENC:def"}}}""");

        var entry = root["Providers"]!["copilot"]!;
        Assert.Equal("ENC:abc", entry["OAuthRefreshToken"]!.GetValue<string>());
        Assert.Equal("ENC:def", entry["OAuthTokenExpiry"]!.GetValue<string>());
    }

    [Fact]
    public void The_exempt_leaf_does_not_exempt_its_siblings()
    {
        var root = Write($$$$"""{"Providers":{"copilot":{"OAuthTokenExpiry":"{{{{Expiry}}}}","OAuthRefreshToken":"r","Type":"t"}}}""");

        var entry = root["Providers"]!["copilot"]!;
        Assert.Equal(Expiry, entry["OAuthTokenExpiry"]!.GetValue<string>());
        Assert.Equal("ENC:r", entry["OAuthRefreshToken"]!.GetValue<string>());
        Assert.Equal("ENC:t", entry["Type"]!.GetValue<string>());
    }

    private string LeafAfterWrite(string json, string leafPath)
    {
        JsonNode? node = Write(json);
        // A flattened root key holds dots or colons in one segment, so the caller never passes one here.
        foreach (var segment in leafPath.Split('.'))
        {
            node = node is JsonArray array
                ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]
                : node![segment];
        }

        return node!.GetValue<string>();
    }

    private JsonObject Write(string json)
    {
        var path = Path.Combine(_dir.Path, "secrets.json");
        SecretsFileWriter.Write(path, json, new PrefixProtector());
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    // No key: the tests read the marker, not ciphertext.
    private sealed class PrefixProtector : ISecretsProtector
    {
        public string Protect(string plaintext) => "ENC:" + plaintext;

        public string Unprotect(string ciphertext) => ciphertext["ENC:".Length..];
    }
}
