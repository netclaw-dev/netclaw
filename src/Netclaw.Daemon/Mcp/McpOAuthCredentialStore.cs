// -----------------------------------------------------------------------
// <copyright file="McpOAuthCredentialStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed class McpOAuthRetiredCredentialWriterException(string message) : InvalidOperationException(message);

internal sealed record McpOAuthClientIdentity(
    string? ClientId,
    string? ClientSecret,
    bool DynamicClientRegistration);

/// <summary>
/// Per-connection SDK token cache. Unpublished candidates keep tokens local;
/// the published cache persists refreshes before returning to the SDK.
/// </summary>
internal sealed class McpOAuthTokenCache : ITokenCache
{
    private readonly McpOAuthCredentialStore _store;

    internal McpOAuthTokenCache(
        McpOAuthCredentialStore store,
        McpServerName serverName,
        string canonicalResource,
        McpOAuthClientIdentity identity,
        McpOAuthTokenSet? credentials,
        int baseRevision,
        bool explicitAuthorization)
    {
        _store = store;
        ServerName = serverName;
        CanonicalResource = canonicalResource;
        Identity = identity;
        Credentials = credentials;
        BaseRevision = baseRevision;
        ExplicitAuthorization = explicitAuthorization;
    }

    internal McpServerName ServerName { get; }

    internal string CanonicalResource { get; }

    internal McpOAuthClientIdentity Identity { get; set; }

    internal McpOAuthTokenSet? Credentials { get; set; }

    internal int BaseRevision { get; set; }

    internal bool ExplicitAuthorization { get; }

    internal bool Dirty { get; set; }

    internal bool Published { get; set; }

    internal bool Retired { get; set; }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        => new(_store.ReadTokens(this, cancellationToken));

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        _store.StoreTokens(this, tokens, cancellationToken);
        return default;
    }
}

/// <summary>
/// Durable authority for active MCP OAuth credentials. The daemon lifecycle
/// gate publishes one cache owner; unpublished authorization state stays local.
/// </summary>
internal sealed class McpOAuthCredentialStore
{
    internal const string SectionKey = "McpOAuthTokens";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ISecretsProtector _protector;
    private readonly ILogger<McpOAuthCredentialStore> _logger;
    private readonly ConcurrentDictionary<McpServerName, ServerCredentialState> _servers = new();

    public McpOAuthCredentialStore(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ISecretsProtector protector,
        ILogger<McpOAuthCredentialStore> logger)
    {
        _paths = paths;
        _timeProvider = timeProvider;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _logger = logger;
        LoadDurableState();
    }

    public static string CanonicalizeResource(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("MCP OAuth resource must be an absolute URI.", nameof(endpoint));

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (builder.Uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }

    public McpOAuthTokenCache CreateTokenCache(
        McpServerName serverName,
        string resourceIdentity,
        string? configuredClientId,
        bool explicitAuthorization)
    {
        var canonicalResource = CanonicalizeResource(resourceIdentity);
        var state = GetState(serverName);
        lock (state.Sync)
        {
            var active = GetBoundOrMigrateLegacy(
                serverName,
                state,
                canonicalResource,
                configuredClientId);
            McpOAuthClientIdentity identity;
            if (!string.IsNullOrWhiteSpace(configuredClientId))
            {
                identity = new McpOAuthClientIdentity(configuredClientId, null, false);
            }
            else if (active is { ClientId: not null })
            {
                // Rebuild the provider identity from the persisted record whenever a client
                // id exists, regardless of the DCR flag. Records written when the SDK ran its
                // own dynamic registration persist the SDK-resolved client id without the DCR
                // marker; discarding it on restart made SDK 2.0 skip the refresh path
                // entirely ("null authorization result" on every later expiry).
                identity = new McpOAuthClientIdentity(
                    active.ClientId,
                    active.ClientSecret?.Value,
                    active.DynamicClientRegistration);
            }
            else
            {
                identity = new McpOAuthClientIdentity(null, null, false);
            }

            return new McpOAuthTokenCache(
                this,
                serverName,
                canonicalResource,
                identity,
                explicitAuthorization ? null : active,
                state.Revision,
                explicitAuthorization);
        }
    }

    public void Publish(McpOAuthTokenCache cache, CancellationToken cancellationToken)
    {
        var state = GetState(cache.ServerName);
        lock (state.Sync)
        {
            ThrowIfRetired(cache);
            if (cache.ExplicitAuthorization && cache.Credentials is null)
                throw new InvalidOperationException("OAuth authorization completed without storing credentials.");
            if (!cache.ExplicitAuthorization && cache.Dirty && cache.BaseRevision != state.Revision)
            {
                throw new McpOAuthRetiredCredentialWriterException(
                    "Active OAuth credentials changed while the replacement connection initialized.");
            }

            if (cache.Dirty)
            {
                var replacement = Clone(cache.Credentials)!;
                if (replacement.RefreshToken is null
                    && CanRetainRefreshToken(state.Active, replacement))
                    replacement.RefreshToken = state.Active!.RefreshToken;
                Persist(cache.ServerName, replacement, cancellationToken);
                state.Active = replacement;
                state.Revision++;
            }
            else
            {
                cache.Credentials = IsBound(state.Active, cache.CanonicalResource)
                    ? Clone(state.Active)
                    : null;
            }

            if (state.PublishedCache is { } previous && !ReferenceEquals(previous, cache))
                previous.Retired = true;
            state.PublishedCache = cache;
            cache.BaseRevision = state.Revision;
            cache.Published = true;
        }
    }

    public void Discard(McpOAuthTokenCache cache)
    {
        var state = GetState(cache.ServerName);
        lock (state.Sync)
        {
            if (!cache.Published)
                cache.Retired = true;
        }
    }

    public McpOAuthTokenSet? GetBoundActive(McpServerName serverName, string resourceIdentity)
    {
        var canonical = CanonicalizeResource(resourceIdentity);
        var state = GetState(serverName);
        lock (state.Sync)
            return IsBound(state.Active, canonical) ? Clone(state.Active) : null;
    }

    public bool HasAnyActive(McpServerName serverName)
    {
        var state = GetState(serverName);
        lock (state.Sync)
            return state.Active is not null;
    }

    public bool RequiresAuthorization(McpServerName serverName, string resourceIdentity)
    {
        var active = GetBoundActive(serverName, resourceIdentity);
        return active is null
               || active.ExpiresAt is { } expiresAt
               && expiresAt <= _timeProvider.GetUtcNow()
               && active.RefreshToken is null;
    }

    /// <summary>
    /// Drops a client identity the authorization server has rejected as
    /// <c>invalid_client</c>, so the next explicit authorization registers a fresh one.
    /// Tokens are left alone. This replaces a persisted "rejected id" marker: a provider
    /// that deletes a client registration would otherwise leave every future
    /// authorization reusing an id the server will never accept again.
    /// </summary>
    public void ForgetClientIdentity(
        McpServerName serverName,
        string resourceIdentity,
        CancellationToken cancellationToken)
    {
        var canonical = CanonicalizeResource(resourceIdentity);
        var state = GetState(serverName);
        lock (state.Sync)
        {
            if (!IsBound(state.Active, canonical)
                || state.Active is not { DynamicClientRegistration: true, ClientId: not null })
                return;

            var replacement = Clone(state.Active)!;
            replacement.ClientId = null;
            replacement.ClientSecret = null;
            replacement.DynamicClientRegistration = false;
            Persist(serverName, replacement, cancellationToken);
            state.Active = replacement;
            state.Revision++;
            if (state.PublishedCache is { } published)
            {
                published.Credentials = Clone(replacement);
                published.BaseRevision = state.Revision;
            }

            _logger.LogWarning(
                "Discarded the rejected OAuth client identity for MCP server '{Name}'. " +
                "The next authorization will register a new client.",
                serverName.Value);
        }
    }

    internal McpOAuthTokenSet? GetActiveForTests(McpServerName serverName)
    {
        var state = GetState(serverName);
        lock (state.Sync)
            return Clone(state.Active);
    }

    internal McpOAuthClientIdentity GetIdentity(McpOAuthTokenCache cache)
    {
        var state = GetState(cache.ServerName);
        lock (state.Sync)
            return cache.Identity;
    }

    internal TokenContainer? ReadTokens(McpOAuthTokenCache cache, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetState(cache.ServerName);
        lock (state.Sync)
        {
            if (!IsBound(cache.Credentials, cache.CanonicalResource))
                return null;
            return ToTokenContainer(cache.Credentials!, cache.Identity);
        }
    }

    internal void StoreTokens(
        McpOAuthTokenCache cache,
        TokenContainer tokens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetState(cache.ServerName);
        lock (state.Sync)
        {
            ThrowIfRetired(cache);
            if (cache.Published && !ReferenceEquals(state.PublishedCache, cache))
                throw new McpOAuthRetiredCredentialWriterException(
                    "A retired OAuth connection attempted to replace active credentials.");
            if (!cache.Published
                && !cache.ExplicitAuthorization
                && cache.BaseRevision != state.Revision)
            {
                throw new McpOAuthRetiredCredentialWriterException(
                    "Active OAuth credentials changed while the replacement connection initialized.");
            }

            var replacement = CreateReplacement(tokens, cache.Credentials, cache.Identity, cache.CanonicalResource);
            if (cache.Published || !cache.ExplicitAuthorization)
            {
                Persist(cache.ServerName, replacement, cancellationToken);
                state.Active = replacement;
                state.Revision++;
                cache.BaseRevision = state.Revision;
                if (!cache.Published
                    && state.PublishedCache is { } published)
                {
                    published.Credentials = Clone(replacement);
                    published.BaseRevision = state.Revision;
                }
            }

            cache.Credentials = replacement;
            cache.Dirty = cache.ExplicitAuthorization && !cache.Published;
        }
    }

    /// <summary>
    /// Adopts a client identity obtained by <see cref="McpOAuthClientRegistrar"/>. The
    /// identity reaches disk with the first token store, which is also the first moment
    /// it is known to work.
    /// </summary>
    internal void AdoptClientIdentity(McpOAuthTokenCache cache, McpOAuthClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var state = GetState(cache.ServerName);
        lock (state.Sync)
        {
            ThrowIfRetired(cache);
            cache.Identity = identity;
        }
    }

    private McpOAuthTokenSet CreateReplacement(
        TokenContainer tokens,
        McpOAuthTokenSet? retainedFrom,
        McpOAuthClientIdentity identity,
        string canonicalResource)
    {
        var obtainedAt = tokens.ObtainedAt == default ? _timeProvider.GetUtcNow() : tokens.ObtainedAt;

        // Netclaw's own registered identity wins when present. But when it is absent —
        // e.g. the SDK performed its own dynamic client registration because Netclaw's
        // registrar returned null (server does not advertise RFC 7591 support) or a
        // client-metadata document supplied the id — the SDK's TokenContainer carries
        // the client id/secret precisely so a durable cache survives a restart. Persisting
        // a null client identity here made every cold-start refresh fall through to a
        // new interactive authorization (the "null authorization result" loop observed on
        // the Atlassian MCP nightly runs).
        var clientId = identity.ClientId ?? tokens.ClientId;
        var clientSecret = identity.ClientSecret ?? tokens.ClientSecret;
        var dynamicRegistration = identity.DynamicClientRegistration
            || identity.ClientId is null && !string.IsNullOrWhiteSpace(tokens.ClientId);

        var replacement = new McpOAuthTokenSet
        {
            AccessToken = new SensitiveString(tokens.AccessToken),
            ExpiresAt = tokens.ExpiresIn is { } expiresIn
                ? obtainedAt.AddSeconds(expiresIn)
                : null,
            TokenType = string.IsNullOrWhiteSpace(tokens.TokenType) ? "Bearer" : tokens.TokenType,
            Scope = tokens.Scope,
            ObtainedAt = obtainedAt,
            ClientId = clientId,
            ClientSecret = clientSecret is null ? null : new SensitiveString(clientSecret),
            DynamicClientRegistration = dynamicRegistration,
            ResourceIdentity = canonicalResource,

            // Prefer what the SDK just reported; fall back to the identity we registered
            // with. The SDK omits these on a container it did not build itself.
            AuthorizationServer = tokens.AuthorizationServer,
            TokenEndpointAuthMethod = tokens.TokenEndpointAuthMethod,
        };
        replacement.RefreshToken = tokens.RefreshToken is not null
            ? new SensitiveString(tokens.RefreshToken)
            : CanRetainRefreshToken(retainedFrom, replacement)
                ? retainedFrom!.RefreshToken
                : null;
        return replacement;
    }

    /// <summary>
    /// A refresh token belongs to the issuer that minted it. Carrying one onto a record bound
    /// to a different authorization server would send it to a server that never issued it.
    /// </summary>
    private static bool CanRetainRefreshToken(McpOAuthTokenSet? current, McpOAuthTokenSet replacement)
        => current?.RefreshToken is not null
           && string.Equals(current.ResourceIdentity, replacement.ResourceIdentity, StringComparison.Ordinal)
           && string.Equals(current.ClientId, replacement.ClientId, StringComparison.Ordinal)
           && string.Equals(current.AuthorizationServer, replacement.AuthorizationServer, StringComparison.Ordinal)
           && current.DynamicClientRegistration == replacement.DynamicClientRegistration;

    private TokenContainer ToTokenContainer(McpOAuthTokenSet credentials, McpOAuthClientIdentity identity)
    {
        // Records written before ObtainedAt existed deserialize it as 0001-01-01. Anchoring
        // the lifetime at "now" keeps ExpiresAt authoritative; measuring from the default
        // produces ~6.4e10 seconds, which saturates the int conversion to int.MaxValue and
        // makes the SDK treat every migrated credential as permanently expired.
        var obtainedAt = credentials.ObtainedAt == default
            ? _timeProvider.GetUtcNow()
            : credentials.ObtainedAt;
        var expiresIn = credentials.ExpiresAt is { } expiresAt
            ? (int)Math.Clamp((expiresAt - obtainedAt).TotalSeconds, 0, int.MaxValue)
            : (int?)null;
        return new TokenContainer
        {
            TokenType = credentials.TokenType,
            AccessToken = credentials.AccessToken.Value,
            RefreshToken = credentials.RefreshToken?.Value,
            ExpiresIn = expiresIn,
            ObtainedAt = obtainedAt,
            Scope = credentials.Scope,

            // SDK 2.0 refuses to redeem the refresh token unless the container reports the
            // same registration the provider holds. Omitting any of these four makes every
            // refresh fall through to interactive authorization. The client id and secret
            // come from the identity the provider was built with, not from disk: a pinned
            // OAuthClientId suppresses the stored secret, and the two must agree.
            ClientId = identity.ClientId,
            ClientSecret = identity.ClientSecret,
            AuthorizationServer = credentials.AuthorizationServer,
            TokenEndpointAuthMethod = credentials.TokenEndpointAuthMethod,
        };
    }

    private void Persist(
        McpServerName serverName,
        McpOAuthTokenSet credentials,
        CancellationToken cancellationToken)
    {
        SecretsFileWriter.Update<object?>(
            _paths.SecretsPath,
            (root, _) =>
            {
                var section = root[SectionKey]?.AsObject() ?? [];
                root[SectionKey] = section;
                section[serverName.Value] = JsonSerializer.SerializeToNode(credentials, JsonOptions);
                return (root, null);
            },
            _protector,
            JsonOptions,
            cancellationToken);
    }

    private void LoadDurableState()
    {
        if (!File.Exists(_paths.SecretsPath))
            return;

        Dictionary<McpServerName, McpOAuthTokenSet> loaded;
        try
        {
            loaded = SecretsFileWriter.Update<Dictionary<McpServerName, McpOAuthTokenSet>>(
                _paths.SecretsPath,
                (root, _) =>
                {
                    var result = new Dictionary<McpServerName, McpOAuthTokenSet>();
                    if (root[SectionKey] is not JsonObject section)
                        return (null, result);
                    foreach (var (name, node) in section)
                    {
                        var credentials = node?.Deserialize<McpOAuthTokenSet>(JsonOptions);
                        if (credentials is not null)
                            result[new McpServerName(name)] = credentials;
                    }
                    return (null, result);
                },
                _protector,
                JsonOptions);
        }
        catch (Exception ex)
        {
            // This runs in a singleton constructor and decrypts the whole secrets file, so
            // one unreadable leaf anywhere in it would otherwise take down daemon startup.
            // Losing cached credentials costs a reauthorization; losing the daemon costs
            // every channel, webhook, and schedule.
            // The secrets file is decrypted before this callback runs, so a malformed-JSON or
            // decryption exception can in principle echo a fragment of plaintext credential
            // content. Redact before logging, same as the OAuth HTTP-body-echo case.
            _logger.LogError(SecretOutputRedactor.RedactForLogging(ex),
                "Failed to load MCP OAuth credentials from {Path}. " +
                "Affected MCP servers will require reauthorization.",
                _paths.SecretsPath);
            return;
        }

        foreach (var (name, credentials) in loaded)
            _servers[name] = new ServerCredentialState(credentials);
        if (loaded.Count > 0)
            _logger.LogDebug("Loaded MCP OAuth credentials for {Count} server(s)", loaded.Count);
    }

    private ServerCredentialState GetState(McpServerName serverName)
        => _servers.GetOrAdd(serverName, static _ => new ServerCredentialState(null));

    private McpOAuthTokenSet? GetBoundOrMigrateLegacy(
        McpServerName serverName,
        ServerCredentialState state,
        string canonicalResource,
        string? configuredClientId)
    {
        if (IsBound(state.Active, canonicalResource))
            return Clone(state.Active);
        if (state.Active is not { ResourceIdentity: null, McpServerUrl: not null } legacy)
            return null;

        string legacyResource;
        try
        {
            legacyResource = CanonicalizeResource(legacy.McpServerUrl);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(SecretOutputRedactor.RedactForLogging(ex),
                "Legacy MCP OAuth credentials for '{Name}' have an invalid resource binding",
                serverName.Value);
            return null;
        }

        if (!IsEquivalentResource(legacyResource, canonicalResource))
        {
            // Silence here reads as "no credentials" and sends the operator to
            // reauthorization with no way to see why the stored ones were withheld.
            _logger.LogWarning(
                "Legacy MCP OAuth credentials for '{Name}' are bound to {LegacyResource}, " +
                "which does not match the configured endpoint {ConfiguredResource}. " +
                "Authorization is required for the configured endpoint.",
                serverName.Value,
                legacyResource,
                canonicalResource);
            return null;
        }

        var migrated = Clone(legacy)!;
        migrated.ResourceIdentity = canonicalResource;

        // McpServerUrl stays populated. Dropping it made migration a one-way transform: an
        // endpoint corrected afterwards had no second chance to match, and a rollback to a
        // release that reads this field lost the binding entirely.
        if (migrated.ObtainedAt == default)
            migrated.ObtainedAt = _timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(configuredClientId)
            && !string.IsNullOrWhiteSpace(migrated.ClientId))
            migrated.DynamicClientRegistration = true;
        Persist(serverName, migrated, CancellationToken.None);
        state.Active = migrated;
        state.Revision++;
        _logger.LogInformation(
            "Migrated legacy MCP OAuth credentials for '{Name}' after exact resource match",
            serverName.Value);
        return Clone(migrated);
    }

    /// <summary>
    /// Whether a legacy <c>McpServerUrl</c> may be migrated onto the configured endpoint.
    /// Legacy records stored the RFC 8707 resource indicator, not the endpoint, so an
    /// ordinal match is stricter than the data supports. Scheme and authority must still
    /// agree exactly — a stored token's audience is its origin, and bridging origins would
    /// hand credentials to a different host.
    /// </summary>
    private static bool IsEquivalentResource(string legacy, string configured)
    {
        if (string.Equals(legacy, configured, StringComparison.Ordinal))
            return true;

        if (!Uri.TryCreate(legacy, UriKind.Absolute, out var legacyUri)
            || !Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri))
            return false;

        if (!string.Equals(legacyUri.Scheme, configuredUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(legacyUri.Authority, configuredUri.Authority, StringComparison.OrdinalIgnoreCase))
            return false;

        var legacyPath = legacyUri.AbsolutePath.TrimEnd('/');
        var configuredPath = configuredUri.AbsolutePath.TrimEnd('/');

        // Trailing slash and path case describe the same endpoint. The query still has to
        // agree — it can select a tenant, and a different tenant is a different resource.
        if (string.Equals(legacyPath, configuredPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(legacyUri.Query, configuredUri.Query, StringComparison.Ordinal))
            return true;

        // Providers commonly publish the resource indicator as the bare origin while the
        // MCP endpoint sits on a path beneath it, so narrowing origin -> path is accepted.
        // A path-scoped credential is never widened to a sibling path.
        //
        // The configured side must carry no query. A legacy bare origin is ambiguous: it
        // means either the authorization server declared an origin-wide audience, or the
        // previous release fell back to the configured URL because the server published no
        // resource at all — in which case the endpoint has since been repointed. Those are
        // indistinguishable in the record, and a query can select a tenant, so a credential
        // of unknown scope is not bound to a tenant-scoped endpoint.
        return legacyPath.Length == 0
               && legacyUri.Query.Length == 0
               && configuredUri.Query.Length == 0;
    }

    private static bool IsBound(McpOAuthTokenSet? credentials, string canonicalResource)
        => credentials?.ResourceIdentity is { } binding
           && string.Equals(binding, canonicalResource, StringComparison.Ordinal);

    private static void ThrowIfRetired(McpOAuthTokenCache cache)
    {
        if (cache.Retired)
            throw new McpOAuthRetiredCredentialWriterException(
                "A retired OAuth connection attempted to replace active credentials.");
    }

    private static McpOAuthTokenSet? Clone(McpOAuthTokenSet? credentials)
        => credentials is null
            ? null
            : JsonSerializer.Deserialize<McpOAuthTokenSet>(
                JsonSerializer.Serialize(credentials, JsonOptions), JsonOptions)!;

    private sealed class ServerCredentialState(McpOAuthTokenSet? active)
    {
        public object Sync { get; } = new();

        public McpOAuthTokenSet? Active { get; set; } = active;

        public int Revision { get; set; }

        public McpOAuthTokenCache? PublishedCache { get; set; }
    }
}
