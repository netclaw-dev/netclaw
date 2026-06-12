// -----------------------------------------------------------------------
// <copyright file="RemoteChatChannelBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Configuration.Http;
using Netclaw.Tools;

namespace Netclaw.Daemon.Configuration;

public static class RemoteChatChannelRegistrationExtensions
{
    /// <summary>
    /// Registers a remote chat channel (Slack/Discord/Mattermost shape):
    /// binds <typeparamref name="TOptions"/> from the configuration section
    /// named after <paramref name="channelType"/>, registers the channel
    /// descriptor (always — the registry lists disabled channels too), and,
    /// when enabled, registers <typeparamref name="TChannel"/> as the keyed
    /// <see cref="IChannel"/> plus its <see cref="IHostedService"/> forward.
    /// All subsequent <c>With*</c> builder calls are no-ops when the channel
    /// is disabled, mirroring the early-return of the per-channel
    /// registration methods this builder replaces.
    /// </summary>
    public static RemoteChatChannelBuilder<TChannel, TOptions> AddRemoteChatChannel<TChannel, TOptions>(
        this IServiceCollection services,
        ChannelType channelType,
        IConfiguration configuration,
        IReadOnlySet<ChannelOutputEffectKind>? additionalOutputEffects = null)
        where TChannel : class, IChannel
        where TOptions : class, IRemoteChatChannelOptions, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Section name and display name are both the enum name ("Slack",
        // "Discord", "Mattermost"); the keyed-service key is the descriptor
        // wire value ("slack", "discord", "mattermost").
        var displayName = channelType.ToString();
        var options = configuration.GetSection(displayName).Get<TOptions>() ?? new TOptions();
        services.AddSingleton(options);
        services.AddChannelRegistry();
        services.AddChannelDescriptorWithRuntimeSnapshot(ChannelDescriptor.CreateRemoteChat(
            channelType,
            displayName,
            options.Enabled,
            options.AllowDirectMessages,
            additionalOutputEffects));

        var channelKey = ChannelDescriptorKey.FromChannelType(channelType).Value;
        var builder = new RemoteChatChannelBuilder<TChannel, TOptions>(services, options, channelKey);

        if (!options.Enabled)
            return builder;

        services.AddKeyedSingleton<IChannel>(channelKey, (sp, _) =>
        {
            // Thread-history fetchers are registered keyed by channel key (see
            // WithThreadHistory): with two or more channels enabled, an unkeyed
            // IThreadHistoryFetcher registration would resolve to the LAST
            // channel's fetcher for every channel, silently cross-wiring thread
            // rehydration. The channel is activated with its own keyed fetcher
            // passed explicitly; every other constructor dependency resolves
            // unkeyed as before. A channel that requires a fetcher but never
            // called WithThreadHistory fails loudly inside CreateInstance.
            var fetcher = sp.GetKeyedService<IThreadHistoryFetcher>(channelKey);
            return fetcher is null
                ? ActivatorUtilities.CreateInstance<TChannel>(sp)
                : ActivatorUtilities.CreateInstance<TChannel>(sp, fetcher);
        });
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(channelKey));
        services.AddSingleton<TChannel>(sp =>
            (TChannel)sp.GetRequiredKeyedService<IChannel>(channelKey));
        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredKeyedService<IChannel>(channelKey));

        AddSharedChannelTools(services);

        return builder;
    }

    /// <summary>
    /// Registers the channel-agnostic tools (send + the two lookups) exactly
    /// once when the first enabled remote chat channel is added, so any future
    /// channel registered through this builder gets them without touching a
    /// hardcoded enablement list. The explicit guard is required because the
    /// <see cref="IChannelTool"/> forwards are factory-based registrations that
    /// <c>TryAddEnumerable</c> cannot deduplicate.
    /// </summary>
    private static void AddSharedChannelTools(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(SendChannelMessageTool)))
            return;

        services.AddSingleton<SendChannelMessageTool>();
        services.AddSingleton<IChannelTool>(sp => sp.GetRequiredService<SendChannelMessageTool>());
        services.AddSingleton<LookupChannelUserTool>();
        services.AddSingleton<IChannelTool>(sp => sp.GetRequiredService<LookupChannelUserTool>());
        services.AddSingleton<LookupChannelDestinationTool>();
        services.AddSingleton<IChannelTool>(sp => sp.GetRequiredService<LookupChannelDestinationTool>());
    }
}

/// <summary>
/// Fluent registration surface for a remote chat channel. Every method
/// registers nothing when the channel is disabled in configuration; the
/// channel descriptor itself was already registered by
/// <see cref="RemoteChatChannelRegistrationExtensions.AddRemoteChatChannel{TChannel, TOptions}"/>.
/// </summary>
public sealed class RemoteChatChannelBuilder<TChannel, TOptions>
    where TChannel : class, IChannel
    where TOptions : class, IRemoteChatChannelOptions, new()
{
    private readonly IServiceCollection _services;
    private readonly TOptions _options;
    private readonly string _channelKey;

    internal RemoteChatChannelBuilder(IServiceCollection services, TOptions options, string channelKey)
    {
        _services = services;
        _options = options;
        _channelKey = channelKey;
    }

    /// <summary>Registers the channel's gateway transport client.</summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithTransport<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
        => AddClient<TService, TImplementation>();

    /// <summary>Registers the channel's reply (thread response) client.</summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithReplyClient<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
        => AddClient<TService, TImplementation>();

    /// <summary>Registers the channel's proactive outbound send client.</summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithOutboundClient<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
        => AddClient<TService, TImplementation>();

    /// <summary>Registers the channel's user/destination lookup client.</summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithLookupClient<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
        => AddClient<TService, TImplementation>();

    /// <summary>
    /// Registers an address resolver both as itself and as a multi-bound
    /// <see cref="IChannelAddressResolver"/> for the registry.
    /// </summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithResolver<TResolver>(
        Func<IServiceProvider, TOptions, TResolver> factory)
        where TResolver : class, IChannelAddressResolver
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_options.Enabled)
            return this;

        _services.AddSingleton<TResolver>(sp => factory(sp, _options));
        _services.AddSingleton<IChannelAddressResolver>(sp => sp.GetRequiredService<TResolver>());
        return this;
    }

    /// <summary>Registers an output renderer for optional output effects.</summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithRenderer<TRenderer>()
        where TRenderer : class, IChannelOutputRenderer
    {
        if (_options.Enabled)
            _services.AddChannelOutputRenderer<TRenderer>();
        return this;
    }

    /// <summary>Registers the channel's reminder target resolver.</summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithReminderResolver<TResolver>()
        where TResolver : class, IReminderTargetResolver
    {
        if (_options.Enabled)
            _services.AddSingleton<IReminderTargetResolver, TResolver>();
        return this;
    }

    /// <summary>
    /// Registers the channel's thread history fetcher, keyed by the channel
    /// key. Keying is load-bearing: an unkeyed registration would make every
    /// channel resolve the LAST registered fetcher in multi-channel hosts.
    /// The channel factory in
    /// <see cref="RemoteChatChannelRegistrationExtensions.AddRemoteChatChannel{TChannel, TOptions}"/>
    /// resolves the keyed fetcher and passes it to the channel constructor.
    /// </summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithThreadHistory(
        Func<IServiceProvider, TOptions, IThreadHistoryFetcher> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_options.Enabled)
            return this;

        _services.AddKeyedSingleton<IThreadHistoryFetcher>(_channelKey, (sp, _) => factory(sp, _options));
        return this;
    }

    /// <summary>
    /// Registers the channel's <see cref="IChannelOutboundClient"/> — the
    /// proactive send implementation (ACL checks, platform post, session
    /// binding) that the generic <c>send_channel_message</c> tool dispatches
    /// to by channel key.
    /// </summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithProactiveSendClient<TClient>(
        Func<IServiceProvider, TOptions, TClient> factory)
        where TClient : class, IChannelOutboundClient
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_options.Enabled)
            return this;

        _services.AddSingleton<TClient>(sp => factory(sp, _options));
        _services.AddSingleton<IChannelOutboundClient>(sp => sp.GetRequiredService<TClient>());
        return this;
    }

    /// <summary>
    /// Registers the concrete user-lookup implementation consumed by the
    /// generic <c>lookup_channel_user</c> tool, plus its
    /// <see cref="IChannelAddressResolver"/> forward for the registry.
    /// </summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithLookupTool<TTool>(
        Func<IServiceProvider, TOptions, TTool> factory)
        where TTool : class, IChannelAddressResolver
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_options.Enabled)
            return this;

        _services.AddSingleton<TTool>(sp => factory(sp, _options));
        _services.AddSingleton<IChannelAddressResolver>(sp => sp.GetRequiredService<TTool>());
        return this;
    }

    /// <summary>
    /// Registers the channel's named file-download HTTP client
    /// (<c>"{key}-files"</c>) with Netclaw identification headers.
    /// </summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithFilesHttpClient(
        Action<TOptions, HttpClient>? configure = null)
    {
        if (!_options.Enabled)
            return this;

        var name = $"{_channelKey}-files";
        var httpBuilder = configure is null
            ? _services.AddHttpClient(name)
            : _services.AddHttpClient(name, client => configure(_options, client));
        httpBuilder.AddNetclawHeaders(name);
        return this;
    }

    /// <summary>
    /// Escape hatch for channel-specific registrations that have no generic
    /// builder method (e.g. SlackNet wiring, the Discord socket client, the
    /// Mattermost API client). Skipped when the channel is disabled.
    /// </summary>
    public RemoteChatChannelBuilder<TChannel, TOptions> WithServices(
        Action<IServiceCollection, TOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_options.Enabled)
            configure(_services, _options);
        return this;
    }

    private RemoteChatChannelBuilder<TChannel, TOptions> AddClient<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        if (_options.Enabled)
            _services.AddSingleton<TService, TImplementation>();
        return this;
    }
}
