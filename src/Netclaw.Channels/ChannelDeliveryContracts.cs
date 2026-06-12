// -----------------------------------------------------------------------
// <copyright file="ChannelDeliveryContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels;

public readonly record struct ChannelDescriptorKey
{
    public ChannelDescriptorKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public static ChannelDescriptorKey Create(string value)
    {
        return new ChannelDescriptorKey(value);
    }

    public static ChannelDescriptorKey FromChannelType(ChannelType channelType)
    {
        return Create(channelType.ToWireValue());
    }

    public override string ToString() => Value;
}

public enum ChannelKind
{
    RemoteChat,
    LocalInteractiveClient
}

[Flags]
public enum ChannelCapabilities
{
    None = 0,
    ReceiveMessages = 1 << 0,
    SendMessages = 1 << 1,
    DirectMessages = 1 << 2,
    ThreadedConversations = 1 << 3,
    InteractiveApproval = 1 << 4,
    FileIngress = 1 << 5,
    FileEgress = 1 << 6,
    ProactiveSend = 1 << 7,
    UserLookup = 1 << 8,
    DestinationLookup = 1 << 9,
    RuntimeHealth = 1 << 10
}

public enum ChannelAddressKind
{
    Destination,
    User,
    Thread,
    DirectMessage,
    LocalSession
}

public enum ChannelToolIntentKind
{
    SendMessage,
    LookupUser,
    LookupDestination
}

public enum ChannelOutputEffectKind
{
    TextMessage,
    MessageUpdate,
    InteractiveApproval,
    FileAttachment,
    ProcessingIndicator,
    Reaction,
    ThreadRename
}

public sealed record ChannelDescriptor(
    ChannelDescriptorKey Key,
    ChannelType ChannelType,
    ChannelKind Kind,
    string DisplayName,
    bool IsEnabled,
    ChannelCapabilities Capabilities,
    IReadOnlySet<ChannelToolIntentKind> ToolIntents,
    IReadOnlySet<ChannelAddressKind> AddressKinds,
    IReadOnlySet<ChannelOutputEffectKind> SupportedOutputEffects)
{
    private static readonly ChannelCapabilities RemoteChatBaseCapabilities =
        ChannelCapabilities.ReceiveMessages
        | ChannelCapabilities.SendMessages
        | ChannelCapabilities.ThreadedConversations
        | ChannelCapabilities.InteractiveApproval
        | ChannelCapabilities.FileIngress
        | ChannelCapabilities.FileEgress
        | ChannelCapabilities.ProactiveSend
        | ChannelCapabilities.UserLookup
        | ChannelCapabilities.DestinationLookup
        | ChannelCapabilities.RuntimeHealth;

    private static readonly IReadOnlySet<ChannelToolIntentKind> RemoteChatToolIntents =
        new HashSet<ChannelToolIntentKind>
        {
            ChannelToolIntentKind.SendMessage,
            ChannelToolIntentKind.LookupUser
        };

    private static readonly IReadOnlySet<ChannelOutputEffectKind> RemoteChatBaseOutputEffects =
        new HashSet<ChannelOutputEffectKind>
        {
            ChannelOutputEffectKind.TextMessage,
            ChannelOutputEffectKind.FileAttachment
        };

    public static ChannelDescriptor CreateRemoteChat(
        ChannelType channelType,
        string displayName,
        bool isEnabled,
        bool allowDirectMessages,
        IReadOnlySet<ChannelOutputEffectKind>? additionalOutputEffects = null)
    {
        var capabilities = RemoteChatBaseCapabilities;
        if (allowDirectMessages)
            capabilities |= ChannelCapabilities.DirectMessages;

        var addressKinds = new HashSet<ChannelAddressKind>
        {
            ChannelAddressKind.Destination,
            ChannelAddressKind.User
        };

        if (allowDirectMessages)
            addressKinds.Add(ChannelAddressKind.DirectMessage);

        var outputEffects = new HashSet<ChannelOutputEffectKind>(RemoteChatBaseOutputEffects);
        if (additionalOutputEffects is not null)
        {
            foreach (var effect in additionalOutputEffects)
                outputEffects.Add(effect);
        }

        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(channelType),
            channelType,
            ChannelKind.RemoteChat,
            displayName,
            isEnabled,
            capabilities,
            RemoteChatToolIntents,
            addressKinds,
            outputEffects);
    }
}

public sealed record ResolvedChannelAddress
{
    public ResolvedChannelAddress(
        ChannelDescriptorKey channelKey,
        ChannelAddressKind addressKind,
        string stableId,
        string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        ChannelKey = channelKey;
        AddressKind = addressKind;
        StableId = stableId;
        DisplayName = displayName;
    }

    public ChannelDescriptorKey ChannelKey { get; init; }

    public ChannelAddressKind AddressKind { get; init; }

    public string StableId { get; init; }

    public string DisplayName { get; init; }
}

public sealed record ChannelAddressResolutionRequest
{
    public ChannelAddressResolutionRequest(
        ChannelDescriptorKey channelKey,
        ChannelAddressKind addressKind,
        string query,
        bool requireSingleMatch = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        ChannelKey = channelKey;
        AddressKind = addressKind;
        Query = query;
        RequireSingleMatch = requireSingleMatch;
    }

    public ChannelDescriptorKey ChannelKey { get; init; }

    public ChannelAddressKind AddressKind { get; init; }

    public string Query { get; init; }

    public bool RequireSingleMatch { get; init; }
}

public enum ChannelAddressResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    Unsupported,

    /// <summary>
    /// The result is an enumeration of every destination the channel can
    /// deliver to (a blank-query listing), not a match against a query.
    /// An empty candidate list is a valid listing: it means the channel is
    /// reachable but has nothing it is currently allowed to deliver to.
    /// </summary>
    Listed
}

public sealed record ChannelAddressResolutionResult
{
    private ChannelAddressResolutionResult(
        ChannelAddressResolutionStatus status,
        IReadOnlyList<ResolvedChannelAddress> candidates,
        string? error = null)
    {
        Status = status;
        Candidates = candidates;
        Error = error;
    }

    public ChannelAddressResolutionStatus Status { get; init; }

    public IReadOnlyList<ResolvedChannelAddress> Candidates { get; init; }

    public string? Error { get; init; }

    public static ChannelAddressResolutionResult Resolved(ResolvedChannelAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.Resolved, [address]);
    }

    public static ChannelAddressResolutionResult NotFound(string? error = null)
    {
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.NotFound, [], error);
    }

    public static ChannelAddressResolutionResult Ambiguous(IReadOnlyList<ResolvedChannelAddress> candidates, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.Ambiguous, candidates, error);
    }

    public static ChannelAddressResolutionResult Unsupported(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.Unsupported, [], error);
    }

    /// <summary>
    /// A blank-query listing of every deliverable destination. Unlike
    /// <see cref="Ambiguous"/>, an empty list is valid here — it means the
    /// channel currently has no destinations it is allowed to deliver to.
    /// </summary>
    public static ChannelAddressResolutionResult Listed(IReadOnlyList<ResolvedChannelAddress> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.Listed, destinations);
    }

    public ResolvedChannelAddress RequireSingle()
    {
        return Status == ChannelAddressResolutionStatus.Resolved && Candidates.Count == 1
            ? Candidates[0]
            : throw new InvalidOperationException(Error ?? $"Address resolution did not produce a single result. Status: {Status}.");
    }
}

public sealed record ChannelDeliveryTarget
{
    public ChannelDeliveryTarget(
        ChannelDescriptorKey channelKey,
        ResolvedChannelAddress destination,
        string? threadOrRootId = null)
    {
        if (!destination.ChannelKey.Equals(channelKey))
        {
            throw new ArgumentException(
                $"Destination channel key '{destination.ChannelKey}' does not match delivery target channel key '{channelKey}'.",
                nameof(destination));
        }

        ChannelKey = channelKey;
        Destination = destination;
        ThreadOrRootId = threadOrRootId;
    }

    public ChannelDescriptorKey ChannelKey { get; init; }

    public ResolvedChannelAddress Destination { get; init; }

    public string? ThreadOrRootId { get; init; }
}

public enum ChannelOutputRenderStatus
{
    Rendered,
    IgnoredUnsupported
}

public enum ChannelOutputRequirement
{
    Optional,
    Required
}

public sealed record ChannelOutputRenderResult(ChannelOutputRenderStatus Status, string? Detail = null)
{
    public static readonly ChannelOutputRenderResult Rendered = new(ChannelOutputRenderStatus.Rendered);
}

public sealed record ChannelOutputRenderRequest(
    ChannelDeliveryTarget Target,
    SessionOutput Output,
    ChannelOutputEffectKind EffectKind,
    ChannelOutputRequirement Requirement = ChannelOutputRequirement.Optional)
{
    public bool IsRequired => Requirement == ChannelOutputRequirement.Required;
}

public interface IChannelOutputRenderer
{
    ChannelDescriptorKey Key { get; }

    ValueTask RenderAsync(
        ChannelOutputRenderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A proactive outbound send resolved by the generic <c>send_channel_message</c>
/// tool: post to a channel destination or open a direct-message conversation.
/// </summary>
public sealed record ChannelSendRequest
{
    public ChannelSendRequest(ChannelAddressKind addressKind, string targetId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        AddressKind = addressKind;
        TargetId = targetId;
        Text = text;
    }

    /// <summary>
    /// <see cref="ChannelAddressKind.Destination"/> for channel posts or
    /// <see cref="ChannelAddressKind.DirectMessage"/> for DMs.
    /// </summary>
    public ChannelAddressKind AddressKind { get; init; }

    /// <summary>
    /// Stable platform ID: a channel ID for destination sends, a user ID for
    /// direct-message sends.
    /// </summary>
    public string TargetId { get; init; }

    public string Text { get; init; }
}

/// <summary>
/// Per-channel proactive send implementation dispatched to by channel key from
/// the generic <c>send_channel_message</c> tool. Implementations own the
/// channel's outbound ACL checks (allowed channels/users, direct-message gate),
/// the platform post, and wiring the new thread into the session pipeline.
/// The returned string is the tool result shown to the LLM — either a success
/// summary or an <c>Error: ...</c> message; implementations must not throw for
/// expected send failures.
/// </summary>
public interface IChannelOutboundClient
{
    ChannelDescriptorKey Key { get; }

    Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default);
}

public sealed record ChannelPrincipal(
    string StableId,
    string? DisplayName = null);

public sealed record ChannelActivitySnapshot(
    DateTimeOffset? LastInputAtUtc = null,
    DateTimeOffset? LastOutputAtUtc = null,
    long? InputCount = null,
    long? OutputCount = null);

public sealed record ChannelRuntimeSnapshot(
    ChannelDescriptorKey Key,
    bool IsEnabled,
    ChannelHealthStatus Health,
    string? HealthDetail = null,
    bool? IsConnected = null,
    bool? IsReady = null,
    ChannelPrincipal? Principal = null,
    ChannelActivitySnapshot? Activity = null);

public interface IChannelDescriptorProvider
{
    ChannelDescriptor GetDescriptor();
}

public interface IChannelRuntimeSnapshotProvider
{
    ChannelDescriptorKey Key { get; }

    ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IChannelAddressResolver
{
    ChannelDescriptorKey Key { get; }

    IReadOnlySet<ChannelAddressKind> AddressKinds { get; }

    ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every destination this channel can currently deliver to,
    /// gated by the same ACL checks <see cref="ResolveAsync"/> applies.
    /// Listing exists for destinations only — never users — because the
    /// deliverable destination set is bounded (bot memberships, guild
    /// channels, or a configured allowlist) while user directories are
    /// unbounded and only make sense as server-side searches. Default is
    /// a loud <see cref="ChannelAddressResolutionResult.Unsupported"/>;
    /// resolvers opt in by overriding.
    /// </summary>
    ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported(
            $"Channel '{Key}' does not support destination listing."));
}

public interface IChannelRegistry
{
    IReadOnlyCollection<ChannelDescriptor> ListChannels();

    ChannelDescriptor GetChannel(ChannelDescriptorKey key);

    ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(
        ChannelDescriptorKey key,
        CancellationToken cancellationToken = default);

    IChannelAddressResolver GetResolver(ChannelDescriptorKey key, ChannelAddressKind addressKind);

    IChannelOutputRenderer GetOutputRenderer(ChannelDescriptorKey key);

    /// <summary>
    /// Returns the proactive outbound send client for the channel. Throws
    /// <see cref="InvalidOperationException"/> when the channel is unknown or
    /// no outbound client is registered for it.
    /// </summary>
    IChannelOutboundClient GetOutboundClient(ChannelDescriptorKey key);

    ValueTask<ChannelAddressResolutionResult> ResolveAddressAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every destination the channel can deliver to. Routed through
    /// the same (key, Destination) resolver lookup as
    /// <see cref="ResolveAddressAsync"/>.
    /// </summary>
    ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
        ChannelDescriptorKey key,
        CancellationToken cancellationToken = default);

    ValueTask<ChannelOutputRenderResult> RenderOutputAsync(
        ChannelOutputRenderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, ChannelDescriptor> _descriptors;
    private readonly IReadOnlyCollection<ChannelDescriptor> _sortedDescriptors;
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, IChannelRuntimeSnapshotProvider> _snapshotProviders;
    private readonly IReadOnlyDictionary<(ChannelDescriptorKey Key, ChannelAddressKind AddressKind), IChannelAddressResolver> _addressResolvers;
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, IChannelOutputRenderer> _outputRenderers;
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, IChannelOutboundClient> _outboundClients;

    public ChannelRegistry(
        IEnumerable<IChannelDescriptorProvider> descriptorProviders,
        IEnumerable<IChannelRuntimeSnapshotProvider> snapshotProviders,
        IEnumerable<IChannelAddressResolver>? addressResolvers = null,
        IEnumerable<IChannelOutputRenderer>? outputRenderers = null,
        IEnumerable<IChannelOutboundClient>? outboundClients = null)
    {
        ArgumentNullException.ThrowIfNull(descriptorProviders);
        ArgumentNullException.ThrowIfNull(snapshotProviders);

        _descriptors = BuildDescriptorLookup(descriptorProviders);
        _sortedDescriptors = _descriptors.Values
            .OrderBy(d => d.Key.Value, StringComparer.Ordinal)
            .ToArray();
        _snapshotProviders = BuildSnapshotProviderLookup(snapshotProviders);
        _addressResolvers = BuildAddressResolverLookup(addressResolvers ?? []);
        _outputRenderers = BuildOutputRendererLookup(outputRenderers ?? []);
        _outboundClients = BuildOutboundClientLookup(outboundClients ?? []);
    }

    public IReadOnlyCollection<ChannelDescriptor> ListChannels() => _sortedDescriptors;

    public ChannelDescriptor GetChannel(ChannelDescriptorKey key)
    {
        if (_descriptors.TryGetValue(key, out var descriptor))
            return descriptor;

        throw new InvalidOperationException($"No channel descriptor is registered for key '{key}'.");
    }

    public async ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(
        ChannelDescriptorKey key,
        CancellationToken cancellationToken = default)
    {
        if (!_snapshotProviders.TryGetValue(key, out var provider))
            throw new InvalidOperationException($"No channel runtime snapshot provider is registered for key '{key}'.");

        return await provider.GetSnapshotAsync(cancellationToken);
    }

    public IChannelAddressResolver GetResolver(ChannelDescriptorKey key, ChannelAddressKind addressKind)
    {
        var descriptor = GetChannel(key);
        if (!descriptor.AddressKinds.Contains(addressKind))
            throw new InvalidOperationException($"Channel '{key}' does not support address kind '{addressKind}'.");

        if (_addressResolvers.TryGetValue((key, addressKind), out var resolver))
            return resolver;

        throw new InvalidOperationException(
            $"No channel address resolver is registered for key '{key}' and address kind '{addressKind}'.");
    }

    public IChannelOutputRenderer GetOutputRenderer(ChannelDescriptorKey key)
    {
        _ = GetChannel(key);

        if (_outputRenderers.TryGetValue(key, out var renderer))
            return renderer;

        throw new InvalidOperationException($"No channel output renderer is registered for key '{key}'.");
    }

    public IChannelOutboundClient GetOutboundClient(ChannelDescriptorKey key)
    {
        _ = GetChannel(key);

        if (_outboundClients.TryGetValue(key, out var client))
            return client;

        throw new InvalidOperationException($"No channel outbound client is registered for key '{key}'.");
    }

    public async ValueTask<ChannelAddressResolutionResult> ResolveAddressAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolver = GetResolver(request.ChannelKey, request.AddressKind);
        return await resolver.ResolveAsync(request, cancellationToken);
    }

    public async ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
        ChannelDescriptorKey key,
        CancellationToken cancellationToken = default)
    {
        var resolver = GetResolver(key, ChannelAddressKind.Destination);
        return await resolver.ListDestinationsAsync(cancellationToken);
    }

    public async ValueTask<ChannelOutputRenderResult> RenderOutputAsync(
        ChannelOutputRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = GetChannel(request.Target.ChannelKey);
        if (!descriptor.SupportedOutputEffects.Contains(request.EffectKind))
        {
            var detail = $"Channel '{descriptor.Key}' does not support output effect '{request.EffectKind}'.";
            if (request.IsRequired)
                throw new InvalidOperationException($"{detail} Required output effects cannot be ignored.");

            return new ChannelOutputRenderResult(ChannelOutputRenderStatus.IgnoredUnsupported, detail);
        }

        if (!_outputRenderers.TryGetValue(descriptor.Key, out var renderer))
        {
            throw new InvalidOperationException(
                $"Channel '{descriptor.Key}' declares support for output effect '{request.EffectKind}' " +
                "but no channel output renderer is registered.");
        }

        await renderer.RenderAsync(request, cancellationToken);
        return ChannelOutputRenderResult.Rendered;
    }

    private static IReadOnlyDictionary<ChannelDescriptorKey, ChannelDescriptor> BuildDescriptorLookup(
        IEnumerable<IChannelDescriptorProvider> providers)
    {
        var descriptors = new Dictionary<ChannelDescriptorKey, ChannelDescriptor>();

        foreach (var provider in providers)
        {
            var descriptor = provider.GetDescriptor();
            if (!descriptors.TryAdd(descriptor.Key, descriptor))
                throw new InvalidOperationException($"Duplicate channel descriptor key '{descriptor.Key}' registered.");
        }

        return descriptors;
    }

    private static IReadOnlyDictionary<ChannelDescriptorKey, IChannelRuntimeSnapshotProvider> BuildSnapshotProviderLookup(
        IEnumerable<IChannelRuntimeSnapshotProvider> providers)
    {
        var snapshotProviders = new Dictionary<ChannelDescriptorKey, IChannelRuntimeSnapshotProvider>();

        foreach (var provider in providers)
        {
            if (!snapshotProviders.TryAdd(provider.Key, provider))
                throw new InvalidOperationException($"Duplicate channel runtime snapshot provider key '{provider.Key}' registered.");
        }

        return snapshotProviders;
    }

    private static IReadOnlyDictionary<(ChannelDescriptorKey Key, ChannelAddressKind AddressKind), IChannelAddressResolver> BuildAddressResolverLookup(
        IEnumerable<IChannelAddressResolver> resolvers)
    {
        var addressResolvers = new Dictionary<(ChannelDescriptorKey Key, ChannelAddressKind AddressKind), IChannelAddressResolver>();

        foreach (var resolver in resolvers)
        {
            foreach (var addressKind in resolver.AddressKinds)
            {
                var key = (resolver.Key, addressKind);
                if (!addressResolvers.TryAdd(key, resolver))
                {
                    throw new InvalidOperationException(
                        $"Duplicate channel address resolver key '{resolver.Key}' for address kind '{addressKind}' registered.");
                }
            }
        }

        return addressResolvers;
    }

    private static IReadOnlyDictionary<ChannelDescriptorKey, IChannelOutputRenderer> BuildOutputRendererLookup(
        IEnumerable<IChannelOutputRenderer> renderers)
    {
        var outputRenderers = new Dictionary<ChannelDescriptorKey, IChannelOutputRenderer>();

        foreach (var renderer in renderers)
        {
            if (!outputRenderers.TryAdd(renderer.Key, renderer))
                throw new InvalidOperationException($"Duplicate channel output renderer key '{renderer.Key}' registered.");
        }

        return outputRenderers;
    }

    private static IReadOnlyDictionary<ChannelDescriptorKey, IChannelOutboundClient> BuildOutboundClientLookup(
        IEnumerable<IChannelOutboundClient> clients)
    {
        var outboundClients = new Dictionary<ChannelDescriptorKey, IChannelOutboundClient>();

        foreach (var client in clients)
        {
            if (!outboundClients.TryAdd(client.Key, client))
                throw new InvalidOperationException($"Duplicate channel outbound client key '{client.Key}' registered.");
        }

        return outboundClients;
    }
}

public sealed class StaticChannelDescriptorProvider(ChannelDescriptor descriptor) : IChannelDescriptorProvider
{
    public ChannelDescriptor GetDescriptor() => descriptor;
}

public sealed class DescriptorChannelRuntimeSnapshotProvider : IChannelRuntimeSnapshotProvider
{
    private readonly ChannelDescriptor _descriptor;
    private readonly Func<IEnumerable<IChannel>> _channelsAccessor;

    public DescriptorChannelRuntimeSnapshotProvider(
        ChannelDescriptor descriptor,
        IEnumerable<IChannel> channels)
        : this(descriptor, () => channels)
    {
    }

    public DescriptorChannelRuntimeSnapshotProvider(
        ChannelDescriptor descriptor,
        Func<IEnumerable<IChannel>> channelsAccessor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(channelsAccessor);

        _descriptor = descriptor;
        _channelsAccessor = channelsAccessor;
    }

    public ChannelDescriptorKey Key => _descriptor.Key;

    public async ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!_descriptor.IsEnabled)
        {
            return new ChannelRuntimeSnapshot(
                _descriptor.Key,
                IsEnabled: false,
                ChannelHealthStatus.Degraded,
                HealthDetail: $"{_descriptor.DisplayName} connector is disabled in configuration.",
                IsConnected: false,
                IsReady: false,
                Activity: BuildActivitySnapshot(_descriptor.ChannelType));
        }

        var channel = ResolveRuntimeChannel();
        if (channel is null)
        {
            return _descriptor.Kind == ChannelKind.LocalInteractiveClient
                ? new ChannelRuntimeSnapshot(
                    _descriptor.Key,
                    IsEnabled: true,
                    ChannelHealthStatus.Healthy,
                    IsReady: true,
                    Activity: BuildActivitySnapshot(_descriptor.ChannelType))
                : new ChannelRuntimeSnapshot(
                    _descriptor.Key,
                    IsEnabled: true,
                    ChannelHealthStatus.Disconnected,
                    HealthDetail: $"{_descriptor.DisplayName} connector is enabled but was not registered.",
                    IsConnected: false,
                    IsReady: false,
                    Activity: BuildActivitySnapshot(_descriptor.ChannelType));
        }

        var health = await channel.GetHealthAsync(cancellationToken);
        return new ChannelRuntimeSnapshot(
            _descriptor.Key,
            IsEnabled: true,
            health.Status,
            HealthDetail: health.Detail,
            IsConnected: health.Status != ChannelHealthStatus.Disconnected,
            IsReady: health.Status == ChannelHealthStatus.Healthy,
            Activity: BuildActivitySnapshot(_descriptor.ChannelType));
    }

    private IChannel? ResolveRuntimeChannel()
    {
        IChannel? match = null;
        foreach (var channel in _channelsAccessor())
        {
            if (channel.ChannelType != _descriptor.ChannelType)
                continue;

            if (match is not null)
                throw new InvalidOperationException($"Multiple runtime channels are registered for '{_descriptor.Key}'.");

            match = channel;
        }

        return match;
    }

    private static ChannelActivitySnapshot? BuildActivitySnapshot(ChannelType channelType)
    {
        var metrics = ChannelTelemetry.GetAllSnapshots()
            .FirstOrDefault(snapshot => snapshot.ChannelType == channelType);

        if (metrics is null)
            return null;

        return new ChannelActivitySnapshot(
            InputCount: metrics.EventsReceived,
            OutputCount: metrics.RepliesPosted);
    }
}

public static class ChannelRegistryServiceCollectionExtensions
{
    public static IServiceCollection AddChannelRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IChannelRegistry, ChannelRegistry>();
        return services;
    }

    public static IServiceCollection AddChannelDescriptor(
        this IServiceCollection services,
        ChannelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddSingleton<IChannelDescriptorProvider>(new StaticChannelDescriptorProvider(descriptor));
        return services;
    }

    public static IServiceCollection AddChannelDescriptorWithRuntimeSnapshot(
        this IServiceCollection services,
        ChannelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddChannelDescriptor(descriptor);
        services.AddSingleton<IChannelRuntimeSnapshotProvider>(sp =>
            new DescriptorChannelRuntimeSnapshotProvider(
                descriptor,
                () => sp.GetServices<IChannel>()));
        return services;
    }

    public static IServiceCollection AddChannelAddressResolver<TResolver>(this IServiceCollection services)
        where TResolver : class, IChannelAddressResolver
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TResolver>();
        services.AddSingleton<IChannelAddressResolver>(sp => sp.GetRequiredService<TResolver>());
        return services;
    }

    public static IServiceCollection AddChannelOutputRenderer<TRenderer>(this IServiceCollection services)
        where TRenderer : class, IChannelOutputRenderer
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TRenderer>();
        services.AddSingleton<IChannelOutputRenderer>(sp => sp.GetRequiredService<TRenderer>());
        return services;
    }

    public static IServiceCollection AddTuiChannelDescriptor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddChannelDescriptorWithRuntimeSnapshot(new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(ChannelType.Tui),
            ChannelType.Tui,
            ChannelKind.LocalInteractiveClient,
            "TUI",
            IsEnabled: true,
            ChannelCapabilities.ReceiveMessages
                | ChannelCapabilities.SendMessages
                | ChannelCapabilities.InteractiveApproval
                | ChannelCapabilities.FileEgress
                | ChannelCapabilities.RuntimeHealth,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind> { ChannelAddressKind.LocalSession },
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.TextMessage,
                ChannelOutputEffectKind.InteractiveApproval,
                ChannelOutputEffectKind.FileAttachment
            }));
    }
}

public static class ChannelAddressKindWire
{
    public static string ToWireValue(ChannelAddressKind addressKind)
        => addressKind switch
        {
            ChannelAddressKind.Destination => "destination",
            ChannelAddressKind.User => "user",
            ChannelAddressKind.Thread => "thread",
            ChannelAddressKind.DirectMessage => "direct_message",
            ChannelAddressKind.LocalSession => "local_session",
            _ => addressKind.ToString().ToLowerInvariant()
        };

    public static bool TryParse(string value, out ChannelAddressKind addressKind)
    {
        var normalized = Normalize(value);
        switch (normalized)
        {
            case "destination":
            case "channel":
                addressKind = ChannelAddressKind.Destination;
                return true;
            case "user":
                addressKind = ChannelAddressKind.User;
                return true;
            case "thread":
                addressKind = ChannelAddressKind.Thread;
                return true;
            case "directmessage":
            case "dm":
                addressKind = ChannelAddressKind.DirectMessage;
                return true;
            case "localsession":
                addressKind = ChannelAddressKind.LocalSession;
                return true;
            default:
                addressKind = default;
                return false;
        }
    }

    internal static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        var count = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[count++] = char.ToLowerInvariant(ch);
        }

        return count == 0 ? string.Empty : new string(buffer, 0, count);
    }
}
