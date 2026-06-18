// -----------------------------------------------------------------------
// <copyright file="ConfigValueMetadataProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ConfigValueMetadataProviderTests
{
    [Fact]
    public void Search_brave_api_key_metadata_marks_secret_and_secrets_store()
    {
        var metadata = ConfigValueMetadataProvider.Get<SearchConfig>(nameof(SearchConfig.BraveApiKey));

        Assert.Equal("Search.BraveApiKey", metadata.Key);
        Assert.Equal(ConfigPersistStore.SecretsJson, metadata.PersistTo);
        Assert.True(metadata.IsSecret);
        Assert.Equal(typeof(SensitiveString), metadata.ValueType);
    }

    [Fact]
    public void Search_backend_metadata_marks_config_store()
    {
        var metadata = ConfigValueMetadataProvider.Get<SearchConfig>(nameof(SearchConfig.Backend));

        Assert.Equal("Search.Backend", metadata.Key);
        Assert.Equal(ConfigPersistStore.NetclawJson, metadata.PersistTo);
        Assert.False(metadata.IsSecret);
        Assert.Equal(typeof(SearchBackend), metadata.ValueType);
    }

    [Fact]
    public void Mcp_oauth_tokens_metadata_marks_sidecar_store()
    {
        var metadata = ConfigValueMetadataProvider.Get<McpOAuthTokenSet>(nameof(McpOAuthTokenSet.AccessToken));

        Assert.Equal("AccessToken", metadata.Key);
        Assert.Equal(ConfigPersistStore.McpOAuthTokens, metadata.PersistTo);
        Assert.True(metadata.IsSecret);
    }
}
