using System.Collections.Concurrent;
using ModelContextProtocol.Authentication;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpTokenCacheAdapterTests
{
    private readonly ConcurrentDictionary<McpServerName, McpOAuthTokenSet> _tokens = new();
    private int _persistCallCount;

    private McpTokenCacheAdapter CreateAdapter(string serverName = "test-server")
    {
        return new McpTokenCacheAdapter(
            new McpServerName(serverName),
            _tokens,
            () => Interlocked.Increment(ref _persistCallCount),
            TimeProvider.System);
    }

    [Fact]
    public async Task StoreAndRetrieve_RoundTrips()
    {
        var adapter = CreateAdapter();

        var stored = new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        };

        await adapter.StoreTokensAsync(stored, CancellationToken.None);
        var retrieved = await adapter.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal("Bearer", retrieved.TokenType);
        Assert.Equal("access-123", retrieved.AccessToken);
        Assert.Equal("refresh-456", retrieved.RefreshToken);
        Assert.NotNull(retrieved.ExpiresIn);
        Assert.True(retrieved.ExpiresIn > 0);
    }

    [Fact]
    public async Task GetTokensAsync_WhenEmpty_ReturnsNull()
    {
        var adapter = CreateAdapter();

        var result = await adapter.GetTokensAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task StoreTokensAsync_CallsPersist()
    {
        var adapter = CreateAdapter();

        await adapter.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "tok",
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref _persistCallCount));
    }

    [Fact]
    public async Task StoreTokensAsync_PreservesExistingClientIdAndUrl()
    {
        _tokens[new McpServerName("test-server")] = new McpOAuthTokenSet
        {
            AccessToken = new SensitiveString("old-token"),
            ClientId = "my-client-id",
            McpServerUrl = "https://mcp.example.com",
        };

        var adapter = CreateAdapter();

        await adapter.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "new-token",
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var tokenSet = _tokens[new McpServerName("test-server")];
        Assert.Equal("new-token", tokenSet.AccessToken.Value);
        Assert.Equal("my-client-id", tokenSet.ClientId);
        Assert.Equal("https://mcp.example.com", tokenSet.McpServerUrl);
    }

    [Fact]
    public async Task StoreTokensAsync_PreservesExistingRefreshTokenWhenResponseOmitsIt()
    {
        _tokens[new McpServerName("test-server")] = new McpOAuthTokenSet
        {
            AccessToken = new SensitiveString("old-token"),
            RefreshToken = new SensitiveString("existing-refresh"),
        };

        var adapter = CreateAdapter();

        await adapter.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "new-token",
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var tokenSet = _tokens[new McpServerName("test-server")];
        Assert.Equal("new-token", tokenSet.AccessToken.Value);
        Assert.Equal("existing-refresh", tokenSet.RefreshToken?.Value);
    }
}
