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
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal enum McpOAuthCredentialTarget
{
    Active,
    Pending,
}

internal sealed class McpOAuthStaleCredentialEpochException(string message) : InvalidOperationException(message);

internal sealed class McpOAuthClientContext(
    string? clientId,
    string? clientSecret,
    bool dynamicClientRegistration,
    string canonicalResource,
    string? baseActiveEpoch)
{
    private readonly object _sync = new();
    private string? _clientId = clientId;
    private string? _clientSecret = clientSecret;
    private bool _dynamicClientRegistration = dynamicClientRegistration;
    private McpOAuthCredentialTarget _target = McpOAuthCredentialTarget.Active;
    private string? _flowId;
    private DateTimeOffset? _pendingExpiresAt;
    private string? _expectedActiveEpoch = baseActiveEpoch;
    private bool _withholdAccessToken;
    private bool _publishedOwner;
    private string _ownerEpoch = Guid.NewGuid().ToString("N");

    public string CanonicalResource { get; } = canonicalResource;

    public string? BaseActiveEpoch { get; } = baseActiveEpoch;

    public string OwnerEpoch
    {
        get
        {
            lock (_sync)
                return _ownerEpoch;
        }
    }

    public McpOAuthClientIdentity SnapshotIdentity()
    {
        lock (_sync)
            return new McpOAuthClientIdentity(_clientId, _clientSecret, _dynamicClientRegistration);
    }

    public McpOAuthCredentialView SnapshotCredentialView()
    {
        lock (_sync)
        {
            return new McpOAuthCredentialView(
                _target,
                _flowId,
                _pendingExpiresAt,
                _expectedActiveEpoch,
                _withholdAccessToken,
                _publishedOwner,
                _ownerEpoch);
        }
    }

    public void ConfigureCredentialView(
        McpOAuthCredentialTarget target,
        string? flowId,
        DateTimeOffset? pendingExpiresAt,
        bool withholdAccessToken)
    {
        lock (_sync)
        {
            _target = target;
            _flowId = flowId;
            _pendingExpiresAt = pendingExpiresAt;
            _withholdAccessToken = withholdAccessToken;
        }
    }

    public void CaptureDynamicRegistration(DynamicClientRegistrationResponse response)
    {
        lock (_sync)
        {
            _clientId = response.ClientId;
            _clientSecret = response.ClientSecret;
            _dynamicClientRegistration = true;
        }
    }

    public void MarkTokensStored(McpOAuthCredentialTarget target, string committedEpoch)
    {
        lock (_sync)
        {
            _withholdAccessToken = false;
            if (target is McpOAuthCredentialTarget.Active)
            {
                _expectedActiveEpoch = committedEpoch;
                if (_publishedOwner)
                    _ownerEpoch = committedEpoch;
            }
        }
    }

    public void Activate()
    {
        lock (_sync)
        {
            _target = McpOAuthCredentialTarget.Active;
            _flowId = null;
            _pendingExpiresAt = null;
            _expectedActiveEpoch = _ownerEpoch;
            _withholdAccessToken = false;
            _publishedOwner = true;
        }
    }
}

internal sealed record McpOAuthClientIdentity(
    string? ClientId,
    string? ClientSecret,
    bool DynamicClientRegistration);

internal sealed record McpOAuthCredentialView(
    McpOAuthCredentialTarget Target,
    string? FlowId,
    DateTimeOffset? PendingExpiresAt,
    string? ExpectedActiveEpoch,
    bool WithholdAccessToken,
    bool PublishedOwner,
    string OwnerEpoch);

/// <summary>
/// Shared durable authority for every MCP SDK token cache. Epoch-conditional
/// transactions reject writes from retired connections and stale processes.
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
    private readonly ConcurrentDictionary<McpServerName, McpOAuthCredentialEnvelope> _credentials = new();
    private readonly ConcurrentDictionary<McpServerName, object> _serverLocks = new();

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
        LoadAndRecoverDurableState();
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

    public McpOAuthClientContext CreateContext(
        McpServerName serverName,
        string resourceIdentity,
        string? configuredClientId,
        bool explicitAuthorization)
    {
        var canonicalResource = CanonicalizeResource(resourceIdentity);
        lock (GetServerLock(serverName))
        {
            var envelope = GetEnvelope(serverName);
            var active = IsBound(envelope.Active, canonicalResource) ? envelope.Active : null;
            var baseActiveEpoch = envelope.Active?.CredentialEpoch;
            if (!string.IsNullOrWhiteSpace(configuredClientId))
            {
                return new McpOAuthClientContext(
                    configuredClientId,
                    null,
                    false,
                    canonicalResource,
                    baseActiveEpoch);
            }

            var clientId = active is { DynamicClientRegistration: true } ? active.ClientId : null;
            var clientSecret = active is { DynamicClientRegistration: true } ? active.ClientSecret?.Value : null;
            var dynamic = !string.IsNullOrWhiteSpace(clientId);
            if (explicitAuthorization
                && dynamic
                && string.Equals(envelope.RejectedDynamicClientId, clientId, StringComparison.Ordinal))
            {
                clientId = null;
                clientSecret = null;
                dynamic = false;
            }

            return new McpOAuthClientContext(
                clientId,
                clientSecret,
                dynamic,
                canonicalResource,
                baseActiveEpoch);
        }
    }

    public ITokenCache CreateTokenCache(
        McpServerName serverName,
        string resourceIdentity,
        McpOAuthClientContext context,
        McpOAuthCredentialTarget target,
        string? flowId,
        DateTimeOffset? pendingExpiresAt,
        bool withholdAccessToken)
    {
        var canonical = CanonicalizeResource(resourceIdentity);
        if (!string.Equals(canonical, context.CanonicalResource, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth context resource does not match the token-cache resource.");
        if (target is McpOAuthCredentialTarget.Pending
            && (string.IsNullOrWhiteSpace(flowId) || pendingExpiresAt is null))
            throw new InvalidOperationException("Pending OAuth credentials require a flow identity and expiry.");

        context.ConfigureCredentialView(target, flowId, pendingExpiresAt, withholdAccessToken);
        return new CredentialTokenCache(this, serverName, context);
    }

    public void CaptureDynamicRegistration(
        McpServerName serverName,
        McpOAuthClientContext context,
        DynamicClientRegistrationResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        lock (GetServerLock(serverName))
            context.CaptureDynamicRegistration(response);
    }

    public McpOAuthTokenSet? GetBoundActive(McpServerName serverName, string resourceIdentity)
    {
        var canonical = CanonicalizeResource(resourceIdentity);
        lock (GetServerLock(serverName))
        {
            var active = GetEnvelope(serverName).Active;
            return IsBound(active, canonical) ? active : null;
        }
    }

    public bool HasAnyActive(McpServerName serverName)
    {
        lock (GetServerLock(serverName))
            return GetEnvelope(serverName).Active is not null;
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
    /// Transfers active credential ownership to a newly published ordinary
    /// connection. No record is created for a server that has no OAuth credentials.
    /// </summary>
    public void ClaimActiveEpoch(
        McpServerName serverName,
        McpOAuthClientContext context,
        CancellationToken cancellationToken)
    {
        lock (GetServerLock(serverName))
        {
            var current = GetEnvelope(serverName);
            var view = context.SnapshotCredentialView();
            if (view.Target is not McpOAuthCredentialTarget.Active)
                throw new InvalidOperationException("A pending OAuth view cannot claim the active epoch.");

            var committed = CommitEnvelope(
                serverName,
                current,
                latest =>
                {
                    if (latest.Active is null)
                        return view.ExpectedActiveEpoch is null
                            ? EnvelopeMutation.Unchanged
                            : EnvelopeMutation.Stale("Active OAuth credentials were removed before publication.");
                    if (!IsBound(latest.Active, context.CanonicalResource))
                        return EnvelopeMutation.Unchanged;
                    if (string.Equals(latest.Active.CredentialEpoch, view.OwnerEpoch, StringComparison.Ordinal))
                        return EnvelopeMutation.Unchanged;
                    if (!EpochEquals(latest.Active.CredentialEpoch, view.ExpectedActiveEpoch))
                        return EnvelopeMutation.Stale("Active OAuth credentials changed before candidate publication.");

                    latest.Active.CredentialEpoch = view.OwnerEpoch;
                    return EnvelopeMutation.Changed;
                },
                cancellationToken);

            if (IsBound(committed.Active, context.CanonicalResource)
                && string.Equals(committed.Active!.CredentialEpoch, view.OwnerEpoch, StringComparison.Ordinal))
                context.Activate();
        }
    }

    public void PromotePending(
        McpServerName serverName,
        McpOAuthClientContext context,
        string flowId,
        CancellationToken cancellationToken)
    {
        lock (GetServerLock(serverName))
        {
            var current = GetEnvelope(serverName);
            var view = context.SnapshotCredentialView();
            CommitEnvelope(
                serverName,
                current,
                latest =>
                {
                    var pending = latest.Pending;
                    if (pending is null
                        || !string.Equals(pending.FlowId, flowId, StringComparison.Ordinal)
                        || !string.Equals(pending.Credentials.CredentialEpoch, view.OwnerEpoch, StringComparison.Ordinal))
                    {
                        return EnvelopeMutation.Stale("Pending OAuth credentials no longer belong to this flow epoch.");
                    }
                    if (!EpochEquals(latest.Active?.CredentialEpoch, context.BaseActiveEpoch))
                        return EnvelopeMutation.Stale("Active OAuth credentials changed while authorization was pending.");

                    latest.Active = pending.Credentials;
                    latest.Pending = null;
                    latest.RejectedDynamicClientId = null;
                    return EnvelopeMutation.Changed;
                },
                cancellationToken);
            context.Activate();
        }
    }

    public void RemovePending(McpServerName serverName, string flowId, CancellationToken cancellationToken)
    {
        lock (GetServerLock(serverName))
        {
            var current = GetEnvelope(serverName);
            CommitEnvelope(
                serverName,
                current,
                latest =>
                {
                    if (latest.Pending is null
                        || !string.Equals(latest.Pending.FlowId, flowId, StringComparison.Ordinal))
                        return EnvelopeMutation.Unchanged;
                    latest.Pending = null;
                    return EnvelopeMutation.Changed;
                },
                cancellationToken);
        }
    }

    public void MarkDynamicIdentityRejected(
        McpServerName serverName,
        string resourceIdentity,
        string? configuredClientId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredClientId))
            return;

        var canonical = CanonicalizeResource(resourceIdentity);
        lock (GetServerLock(serverName))
        {
            var current = GetEnvelope(serverName);
            CommitEnvelope(
                serverName,
                current,
                latest =>
                {
                    var active = latest.Active;
                    if (!IsBound(active, canonical)
                        || active is not { DynamicClientRegistration: true }
                        || string.IsNullOrWhiteSpace(active.ClientId))
                        return EnvelopeMutation.Unchanged;

                    latest.RejectedDynamicClientId = active.ClientId;
                    return EnvelopeMutation.Changed;
                },
                cancellationToken);
        }
    }

    internal McpOAuthCredentialEnvelope GetEnvelopeForTests(McpServerName serverName)
    {
        lock (GetServerLock(serverName))
            return Clone(GetEnvelope(serverName));
    }

    private TokenContainer? ReadTokens(
        McpServerName serverName,
        McpOAuthClientContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (GetServerLock(serverName))
        {
            var view = context.SnapshotCredentialView();
            if (view.WithholdAccessToken)
                return null;
            var envelope = GetEnvelope(serverName);
            McpOAuthTokenSet? credentials;
            if (view.Target is McpOAuthCredentialTarget.Pending)
            {
                var pending = envelope.Pending;
                credentials = pending is not null
                              && string.Equals(pending.FlowId, view.FlowId, StringComparison.Ordinal)
                              && string.Equals(pending.Credentials.CredentialEpoch, view.OwnerEpoch, StringComparison.Ordinal)
                    ? pending.Credentials
                    : null;
            }
            else
            {
                credentials = envelope.Active is not null
                              && EpochEquals(envelope.Active.CredentialEpoch, view.ExpectedActiveEpoch)
                    ? envelope.Active
                    : null;
            }

            if (!IsBound(credentials, context.CanonicalResource))
                return null;
            return ToTokenContainer(credentials!);
        }
    }

    private void StoreTokens(
        McpServerName serverName,
        McpOAuthClientContext context,
        TokenContainer tokens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (GetServerLock(serverName))
        {
            var current = GetEnvelope(serverName);
            var view = context.SnapshotCredentialView();
            var identity = context.SnapshotIdentity();
            string? committedEpoch = null;
            CommitEnvelope(
                serverName,
                current,
                latest =>
                {
                    McpOAuthTokenSet? retainedFrom;
                    if (view.Target is McpOAuthCredentialTarget.Active)
                    {
                        if (!EpochEquals(latest.Active?.CredentialEpoch, view.ExpectedActiveEpoch))
                            return EnvelopeMutation.Stale("A retired OAuth connection attempted to replace active credentials.");
                        retainedFrom = IsBound(latest.Active, context.CanonicalResource)
                            ? latest.Active
                            : null;
                        if (latest.Active is not null && retainedFrom is null)
                            return EnvelopeMutation.Stale("Active OAuth credentials are bound to another resource.");
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(view.FlowId) || view.PendingExpiresAt is null)
                            return EnvelopeMutation.Stale("Pending OAuth credential context is incomplete.");
                        if (!EpochEquals(latest.Active?.CredentialEpoch, context.BaseActiveEpoch))
                            return EnvelopeMutation.Stale("Active OAuth credentials changed while authorization was pending.");

                        if (latest.Pending is null)
                        {
                            retainedFrom = IsBound(latest.Active, context.CanonicalResource)
                                ? latest.Active
                                : null;
                        }
                        else if (string.Equals(latest.Pending.FlowId, view.FlowId, StringComparison.Ordinal)
                                 && string.Equals(
                                     latest.Pending.Credentials.CredentialEpoch,
                                     view.OwnerEpoch,
                                     StringComparison.Ordinal)
                                 && IsBound(latest.Pending.Credentials, context.CanonicalResource))
                        {
                            retainedFrom = latest.Pending.Credentials;
                        }
                        else
                        {
                            return EnvelopeMutation.Stale("Another OAuth flow owns the pending credential record.");
                        }
                    }

                    var replacementEpoch = view.Target is McpOAuthCredentialTarget.Pending
                        ? view.OwnerEpoch
                        : view.PublishedOwner
                            ? Guid.NewGuid().ToString("N")
                            : view.ExpectedActiveEpoch ?? view.OwnerEpoch;
                    committedEpoch = replacementEpoch;
                    var replacement = CreateReplacement(
                        tokens,
                        retainedFrom,
                        identity,
                        context,
                        replacementEpoch);
                    if (view.Target is McpOAuthCredentialTarget.Active)
                    {
                        latest.Active = replacement;
                    }
                    else
                    {
                        latest.Pending = new McpOAuthPendingCredential
                        {
                            FlowId = view.FlowId!,
                            ExpiresAt = view.PendingExpiresAt!.Value,
                            Credentials = replacement,
                        };
                    }

                    return EnvelopeMutation.Changed;
                },
                cancellationToken);
            context.MarkTokensStored(view.Target, committedEpoch!);
        }
    }

    private McpOAuthTokenSet CreateReplacement(
        TokenContainer tokens,
        McpOAuthTokenSet? retainedFrom,
        McpOAuthClientIdentity identity,
        McpOAuthClientContext context,
        string credentialEpoch)
    {
        var obtainedAt = tokens.ObtainedAt == default ? _timeProvider.GetUtcNow() : tokens.ObtainedAt;
        return new McpOAuthTokenSet
        {
            AccessToken = new SensitiveString(tokens.AccessToken),
            RefreshToken = tokens.RefreshToken is not null
                ? new SensitiveString(tokens.RefreshToken)
                : retainedFrom?.RefreshToken,
            ExpiresAt = tokens.ExpiresIn is { } expiresIn
                ? obtainedAt.AddSeconds(expiresIn)
                : null,
            TokenType = string.IsNullOrWhiteSpace(tokens.TokenType) ? "Bearer" : tokens.TokenType,
            Scope = tokens.Scope,
            ObtainedAt = obtainedAt,
            ClientId = identity.ClientId,
            ClientSecret = identity.ClientSecret is null ? null : new SensitiveString(identity.ClientSecret),
            DynamicClientRegistration = identity.DynamicClientRegistration,
            ResourceIdentity = context.CanonicalResource,
            CredentialEpoch = credentialEpoch,
        };
    }

    private static TokenContainer ToTokenContainer(McpOAuthTokenSet credentials)
    {
        var expiresIn = credentials.ExpiresAt is { } expiresAt
            ? (int)Math.Max(0, (expiresAt - credentials.ObtainedAt).TotalSeconds)
            : (int?)null;
        return new TokenContainer
        {
            TokenType = credentials.TokenType,
            AccessToken = credentials.AccessToken.Value,
            RefreshToken = credentials.RefreshToken?.Value,
            ExpiresIn = expiresIn,
            ObtainedAt = credentials.ObtainedAt,
            Scope = credentials.Scope,
        };
    }

    private McpOAuthCredentialEnvelope CommitEnvelope(
        McpServerName serverName,
        McpOAuthCredentialEnvelope current,
        Func<McpOAuthCredentialEnvelope, EnvelopeMutation> mutate,
        CancellationToken cancellationToken)
    {
        var outcome = SecretsFileWriter.Update<EnvelopeCommitResult>(
            _paths.SecretsPath,
            (root, _) =>
            {
                var section = root[SectionKey]?.AsObject() ?? [];
                root[SectionKey] = section;
                var latest = ReadEnvelope(section[serverName.Value]) ?? Clone(current);
                var mutation = mutate(latest);
                if (mutation.StaleReason is not null)
                    return (null, new EnvelopeCommitResult(latest, mutation.StaleReason));
                if (!mutation.HasChanges)
                    return (null, new EnvelopeCommitResult(latest, null));

                section[serverName.Value] = JsonSerializer.SerializeToNode(latest, JsonOptions);
                return (root, new EnvelopeCommitResult(latest, null));
            },
            JsonOptions,
            _protector,
            cancellationToken);

        _credentials[serverName] = outcome.Envelope;
        if (outcome.StaleReason is not null)
            throw new McpOAuthStaleCredentialEpochException(outcome.StaleReason);
        return outcome.Envelope;
    }

    private void LoadAndRecoverDurableState()
    {
        if (!File.Exists(_paths.SecretsPath))
            return;

        var loaded = SecretsFileWriter.Update<Dictionary<McpServerName, McpOAuthCredentialEnvelope>>(
            _paths.SecretsPath,
            (root, _) =>
            {
                var result = new Dictionary<McpServerName, McpOAuthCredentialEnvelope>();
                var changed = false;
                if (root[SectionKey] is not JsonObject section)
                    return (null, result);

                foreach (var (name, node) in section)
                {
                    if (node is null)
                        continue;
                    var envelope = ReadEnvelope(node);
                    if (envelope is null)
                        continue;

                    // A pending record cannot resume without its broker flow. A
                    // promoted active record is complete durable state and remains
                    // usable after a crash between promotion and publication.
                    if (envelope.Pending is not null)
                    {
                        envelope.Pending = null;
                        changed = true;
                    }
                    if (envelope.Active is { ResourceIdentity: not null, CredentialEpoch: null })
                    {
                        envelope.Active.CredentialEpoch = Guid.NewGuid().ToString("N");
                        changed = true;
                    }

                    if (changed)
                        section[name] = JsonSerializer.SerializeToNode(envelope, JsonOptions);
                    result[new McpServerName(name)] = envelope;
                }

                return (changed ? root : null, result);
            },
            JsonOptions,
            _protector);

        foreach (var (name, envelope) in loaded)
            _credentials[name] = envelope;
        if (loaded.Count > 0)
            _logger.LogDebug("Loaded MCP OAuth credentials for {Count} server(s)", loaded.Count);
    }

    private object GetServerLock(McpServerName serverName)
        => _serverLocks.GetOrAdd(serverName, static _ => new object());

    private McpOAuthCredentialEnvelope GetEnvelope(McpServerName serverName)
        => _credentials.GetOrAdd(serverName, static _ => new McpOAuthCredentialEnvelope());

    private static bool IsBound(McpOAuthTokenSet? credentials, string canonicalResource)
        => credentials?.ResourceIdentity is { } binding
           && string.Equals(binding, canonicalResource, StringComparison.Ordinal);

    private static bool EpochEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.Ordinal);

    private static McpOAuthCredentialEnvelope? ReadEnvelope(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;
        if (obj.ContainsKey(nameof(McpOAuthCredentialEnvelope.Active))
            || obj.ContainsKey(nameof(McpOAuthCredentialEnvelope.Pending))
            || obj.ContainsKey(nameof(McpOAuthCredentialEnvelope.RejectedDynamicClientId)))
            return obj.Deserialize<McpOAuthCredentialEnvelope>(JsonOptions);

        var legacy = obj.Deserialize<McpOAuthTokenSet>(JsonOptions);
        return legacy is null ? null : new McpOAuthCredentialEnvelope { Active = legacy };
    }

    private static McpOAuthCredentialEnvelope Clone(McpOAuthCredentialEnvelope envelope)
        => JsonSerializer.Deserialize<McpOAuthCredentialEnvelope>(
            JsonSerializer.Serialize(envelope, JsonOptions), JsonOptions)!;

    private sealed record EnvelopeCommitResult(
        McpOAuthCredentialEnvelope Envelope,
        string? StaleReason);

    private readonly record struct EnvelopeMutation(bool HasChanges, string? StaleReason)
    {
        public static EnvelopeMutation Changed => new(true, null);

        public static EnvelopeMutation Unchanged => new(false, null);

        public static EnvelopeMutation Stale(string reason) => new(false, reason);
    }

    private sealed class CredentialTokenCache(
        McpOAuthCredentialStore store,
        McpServerName serverName,
        McpOAuthClientContext context) : ITokenCache
    {
        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
            => new(store.ReadTokens(serverName, context, cancellationToken));

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            store.StoreTokens(serverName, context, tokens, cancellationToken);
            return default;
        }
    }
}
