// -----------------------------------------------------------------------
// <copyright file="ChannelIntegrationRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Discord.WebSocket;
using Mattermost;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Tools;
using Netclaw.Channels.Mattermost.Transport;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Slack.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;
using SlackNet.Interaction.Experimental;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Composition root for the remote chat channel integrations. Each channel
/// is one fluent <c>AddRemoteChatChannel</c> chain; channel-specific
/// registrations that have no generic builder method go through
/// <c>WithServices</c>.
/// </summary>
public static class ChannelIntegrationRegistrationExtensions
{
    public static void AddChannelIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        AddSlackChannel(services, configuration);
        AddDiscordChannel(services, configuration);
        AddMattermostChannel(services, configuration);
    }

    internal static void AddSlackChannel(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRemoteChatChannel<SlackChannel, SlackChannelOptions>(
                ChannelType.Slack,
                configuration,
                new HashSet<ChannelOutputEffectKind> { ChannelOutputEffectKind.ProcessingIndicator })
            // Token validity is NOT checked here: an exception thrown from this
            // registration path aborts host construction and crashes the daemon.
            // A missing/invalid token is handled as a contained channel failure in
            // SlackChannel.StartAsync instead (see issue #1033).
            .WithFilesHttpClient()
            .WithReplyClient<ISlackReplyClient, SlackReplyClient>()
            .WithThreadHistory((sp, options) =>
            {
                var slackApi = sp.GetRequiredService<SlackNet.ISlackApiClient>();
                var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
                var contentScanner = sp.GetRequiredService<IContentScanner>();
                var toolConfig = sp.GetRequiredService<ToolConfig>();
                var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SlackThreadHistoryFetcher>();
                return new SlackThreadHistoryFetcher(
                    slackApi.Conversations,
                    options,
                    httpFactory.CreateClient("slack-files"),
                    contentScanner,
                    toolConfig.AudienceProfiles,
                    modelCapabilities,
                    logger,
                    sp.GetRequiredService<ISessionStorageResolver>());
            })
            .WithOutboundClient<ISlackOutboundClient, SlackOutboundClient>()
            .WithLookupClient<ISlackTargetLookupClient, SlackApiTargetLookupClient>()
            .WithRenderer<SlackProcessingOutputRenderer>()
            .WithReminderResolver<SlackReminderTargetResolver>()
            // The default-channel accessor reads the RUNTIME-resolved ID from
            // SlackChannel (StartAsync resolves DefaultChannelName → ID), not
            // the raw config value — a name-only configuration would otherwise
            // leave the resolver blind to the default channel. The channel is
            // resolved lazily inside the accessor: the channel registry
            // constructs this resolver eagerly, before any channel exists.
            .WithResolver((sp, options) => new SlackTargetResolver(
                sp.GetRequiredService<ISlackTargetLookupClient>(),
                options,
                () => sp.GetRequiredService<SlackChannel>().DefaultChannelId))
            .WithServices((channelServices, options) =>
            {
                channelServices.AddSingleton<SlackApprovalHandler>();
                channelServices.AddSingleton<ISlackTargetResolver>(sp =>
                    sp.GetRequiredService<SlackTargetResolver>());
                channelServices.AddSlackNet(c =>
                {
                    // Placeholder when unconfigured so SlackNet registration does not
                    // NullReferenceException — SlackChannel.StartAsync fails the channel
                    // loud and degrades before this client is ever used.
                    c.UseApiToken(options.BotToken.IsNullOrEmpty()
                        ? "unconfigured"
                        : options.BotToken.Value);

                    if (options.SocketMode)
                        c.UseAppLevelToken(options.AppToken.IsNullOrEmpty()
                            ? "unconfigured"
                            : options.AppToken.Value);

                    c.RegisterEventHandler<MessageEvent, SlackChannel>();
                    c.RegisterEventHandler<AppMention, SlackChannel>();
                    c.ReplaceBlockActionHandling(context =>
                        context.ServiceProvider().GetRequiredService<SlackApprovalHandler>());
                });
            })
            // User lookup is exposed through the generic lookup_channel_user tool.
            // The gateway actor ref and default channel ID are resolved lazily via
            // SlackChannel since they're not available until StartAsync completes.
            // The channel itself is also resolved lazily inside the accessors:
            // the channel registry constructs outbound clients eagerly, and an
            // eager channel resolution would recurse through the registry's own
            // construction (channel → registry → outbound client → channel).
            .WithProactiveSendClient((sp, options) => new SlackProactiveOutboundClient(
                sp.GetRequiredService<ISlackOutboundClient>(),
                options,
                () => sp.GetRequiredService<SlackChannel>().DefaultChannelId,
                () => sp.GetRequiredService<SlackChannel>().Gateway))
            .WithLookupTool((sp, options) =>
            {
                var slackApi = sp.GetRequiredService<SlackNet.ISlackApiClient>();
                var timeProvider = sp.GetRequiredService<TimeProvider>();
                return new LookupSlackUserTool(slackApi.Users, options, timeProvider);
            });
    }

    internal static void AddDiscordChannel(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRemoteChatChannel<DiscordChannel, DiscordChannelOptions>(
                ChannelType.Discord,
                configuration,
                new HashSet<ChannelOutputEffectKind> { ChannelOutputEffectKind.ProcessingIndicator })
            // Token validity is NOT checked here: an exception thrown from this
            // registration path aborts host construction and crashes the daemon.
            // A missing/invalid token is handled as a contained channel failure in
            // DiscordChannel.StartAsync instead (see issue #1033).
            .WithServices((channelServices, _) =>
                channelServices.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
                {
                    GatewayIntents = global::Discord.GatewayIntents.Guilds
                        | global::Discord.GatewayIntents.GuildMessages
                        | global::Discord.GatewayIntents.DirectMessages
                        | global::Discord.GatewayIntents.MessageContent,
                    AlwaysDownloadUsers = false,
                    MessageCacheSize = 100
                })))
            .WithFilesHttpClient()
            .WithTransport<IDiscordGatewayClient, DiscordNetGatewayClient>()
            .WithReplyClient<IDiscordReplyClient, DiscordNetReplyClient>()
            .WithOutboundClient<IDiscordOutboundClient, DiscordNetOutboundClient>()
            .WithLookupClient<IDiscordAddressLookupClient, DiscordNetAddressLookupClient>()
            .WithRenderer<DiscordProcessingOutputRenderer>()
            .WithThreadHistory((sp, options) =>
            {
                var client = sp.GetRequiredService<DiscordSocketClient>();
                var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
                var contentScanner = sp.GetRequiredService<IContentScanner>();
                var toolConfig = sp.GetRequiredService<ToolConfig>();
                var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DiscordThreadHistoryFetcher>();

                return new DiscordThreadHistoryFetcher(
                    client,
                    options,
                    httpFactory.CreateClient("discord-files"),
                    contentScanner,
                    toolConfig.AudienceProfiles,
                    modelCapabilities,
                    logger,
                    sp.GetRequiredService<ISessionStorageResolver>());
            })
            .WithReminderResolver((_, options) => new DiscordReminderTargetResolver(options))
            .WithResolver((sp, options) => new DiscordAddressResolver(
                sp.GetRequiredService<IDiscordAddressLookupClient>(),
                options,
                () => string.IsNullOrWhiteSpace(options.DefaultChannelId)
                    ? null
                    : new DiscordChannelId(options.DefaultChannelId)))
            // The gateway actor ref is resolved lazily via DiscordChannel since it
            // is not available until StartAsync completes. The channel itself is
            // also resolved lazily inside the accessor: DiscordChannel injects
            // IChannelRegistry, which constructs outbound clients eagerly — an
            // eager channel resolution here would recurse through the registry's
            // own construction.
            .WithProactiveSendClient((sp, options) => new DiscordProactiveOutboundClient(
                sp.GetRequiredService<IDiscordOutboundClient>(),
                options,
                () => sp.GetRequiredService<DiscordChannel>().Gateway));
    }

    internal static void AddMattermostChannel(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRemoteChatChannel<MattermostChannel, MattermostChannelOptions>(ChannelType.Mattermost, configuration)
            // Token and server-URL validity are NOT checked here: an exception
            // thrown from this registration path aborts host construction and
            // crashes the daemon. A missing/invalid token or URL is handled as a
            // contained channel failure in MattermostChannel.StartAsync instead
            // (see issue #1033). The fallback values below are only ever
            // materialized for a misconfigured channel, which degrades before the
            // transport client is used.
            .WithServices((channelServices, options) =>
            {
                var serverUrl = MattermostServerUrl(options);
                var botToken = MattermostBotToken(options);
                // MattermostClient rejects empty API keys at construction, and
                // the channel registry constructs the outbound client (and with
                // it this client) eagerly at startup. Use the same
                // "unconfigured" placeholder as the SlackNet registration above
                // so an enabled-but-tokenless channel still constructs — it
                // degrades loudly in MattermostChannel.StartAsync before the
                // client is ever used (see issue #1033).
                channelServices.AddSingleton(_ => new MattermostClient(
                    serverUrl,
                    string.IsNullOrEmpty(botToken) ? "unconfigured" : botToken));

                if (!string.IsNullOrEmpty(options.CallbackUrl))
                    channelServices.AddSingleton(new MattermostCallbackActionStore(TimeProvider.System));
            })
            .WithFilesHttpClient((options, client) =>
            {
                var botToken = MattermostBotToken(options);
                if (!string.IsNullOrEmpty(botToken))
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", botToken);
            })
            .WithTransport<IMattermostGatewayClient, MattermostNetGatewayClient>()
            .WithReplyClient<IMattermostReplyClient, MattermostNetReplyClient>()
            .WithThreadHistory((sp, options) =>
            {
                var client = sp.GetRequiredService<MattermostClient>();
                var contentScanner = sp.GetRequiredService<IContentScanner>();
                var toolConfig = sp.GetRequiredService<ToolConfig>();
                var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MattermostThreadHistoryFetcher>();

                var gatewayClient = sp.GetRequiredService<IMattermostGatewayClient>();

                return new MattermostThreadHistoryFetcher(
                    client,
                    contentScanner,
                    options,
                    MattermostServerUrl(options),
                    () => gatewayClient.BotUserId?.Value,
                    toolConfig.AudienceProfiles,
                    modelCapabilities,
                    logger,
                    sp.GetRequiredService<ISessionStorageResolver>());
            })
            .WithReminderResolver<MattermostReminderTargetResolver>()
            .WithOutboundClient<IMattermostOutboundClient, MattermostNetOutboundClient>()
            .WithResolver((_, options) => new MattermostDestinationAddressResolver(
                options,
                () => string.IsNullOrWhiteSpace(options.DefaultChannelId)
                    ? null
                    : new MattermostChannelId(options.DefaultChannelId)))
            // The gateway actor ref and default channel ID are resolved lazily via
            // MattermostChannel since they're not available until StartAsync
            // completes. The channel itself is also resolved lazily inside the
            // accessors: the channel registry constructs outbound clients eagerly,
            // and an eager channel resolution would recurse through the registry's
            // own construction.
            .WithProactiveSendClient((sp, options) => new MattermostProactiveOutboundClient(
                sp.GetRequiredService<IMattermostOutboundClient>(),
                options,
                () => sp.GetRequiredService<MattermostChannel>().DefaultChannelId,
                () => sp.GetRequiredService<MattermostChannel>().Gateway))
            .WithLookupTool((sp, options) => new LookupMattermostUserTool(
                () => sp.GetRequiredService<MattermostClient>(),
                options));
    }

    private static string MattermostServerUrl(MattermostChannelOptions options)
        => string.IsNullOrWhiteSpace(options.ServerUrl)
            ? "https://mattermost.invalid"
            : options.ServerUrl;

    private static string MattermostBotToken(MattermostChannelOptions options)
        => options.BotToken?.Value ?? string.Empty;
}
