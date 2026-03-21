using System.Collections.Concurrent;
using ModelContextProtocol.Authentication;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Bridges the MCP SDK's <see cref="ITokenCache"/> to Netclaw's existing
/// <see cref="McpOAuthTokenSet"/> persistence. One instance per MCP server.
/// </summary>
internal sealed class McpTokenCacheAdapter : ITokenCache
{
    private readonly string _serverName;
    private readonly ConcurrentDictionary<string, McpOAuthTokenSet> _tokens;
    private readonly Action _persistTokens;
    private readonly TimeProvider _timeProvider;

    public McpTokenCacheAdapter(
        string serverName,
        ConcurrentDictionary<string, McpOAuthTokenSet> tokens,
        Action persistTokens,
        TimeProvider timeProvider)
    {
        _serverName = serverName;
        _tokens = tokens;
        _persistTokens = persistTokens;
        _timeProvider = timeProvider;
    }

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var expiresAt = tokens.ExpiresIn is { } expiresIn
            ? now.AddSeconds(expiresIn)
            : (DateTimeOffset?)null;

        var tokenSet = new McpOAuthTokenSet
        {
            AccessToken = new SensitiveString(tokens.AccessToken),
            RefreshToken = tokens.RefreshToken is not null
                ? new SensitiveString(tokens.RefreshToken)
                : null,
            ExpiresAt = expiresAt,
        };

        // Preserve existing metadata and refresh token when the auth server omits it.
        if (_tokens.TryGetValue(_serverName, out var existing))
        {
            tokenSet.ClientId = existing.ClientId;
            tokenSet.McpServerUrl = existing.McpServerUrl;
            tokenSet.RefreshToken ??= existing.RefreshToken;
        }

        _tokens[_serverName] = tokenSet;
        _persistTokens();

        return default;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!_tokens.TryGetValue(_serverName, out var tokenSet))
            return new ValueTask<TokenContainer?>((TokenContainer?)null);

        var now = _timeProvider.GetUtcNow();
        var expiresIn = tokenSet.ExpiresAt is { } expiresAt
            ? (int)Math.Max(0, (expiresAt - now).TotalSeconds)
            : (int?)null;

        var container = new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = tokenSet.AccessToken.Value,
            RefreshToken = tokenSet.RefreshToken?.Value,
            ExpiresIn = expiresIn,
            ObtainedAt = tokenSet.ExpiresAt is { } ea && expiresIn is { } ei
                ? ea.AddSeconds(-ei)
                : now,
        };

        return new ValueTask<TokenContainer?>(container);
    }
}
