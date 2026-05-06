// -----------------------------------------------------------------------
// <copyright file="MattermostChannelRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Tools;
using Netclaw.Channels.Mattermost.Transport;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Daemon.Configuration;

public static class MattermostChannelRegistrationExtensions
{
    private const string MattermostChannelKey = "mattermost";

    public static void AddMattermostChannelIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var mattermostOptions = configuration.GetSection("Mattermost").Get<MattermostChannelOptions>() ?? new MattermostChannelOptions();
        services.AddSingleton(mattermostOptions);

        if (!mattermostOptions.Enabled)
            return;

        mattermostOptions.BotToken.RequireValid("Mattermost:BotToken");
        var serverUrl = mattermostOptions.ServerUrl
            ?? throw new InvalidOperationException("Mattermost:ServerUrl is required when Mattermost channel is enabled.");

        services.AddSingleton(_ => new MattermostClient(serverUrl, mattermostOptions.BotToken!.Value));

        services.AddHttpClient("mattermost-files");
        services.AddSingleton<IMattermostGatewayClient, MattermostNetGatewayClient>();
        services.AddSingleton<IMattermostReplyClient>(sp =>
        {
            var client = sp.GetRequiredService<MattermostClient>();
            return new MattermostNetReplyClient(client);
        });
        services.AddSingleton<IThreadHistoryFetcher>(sp =>
        {
            var client = sp.GetRequiredService<MattermostClient>();
            var contentScanner = sp.GetRequiredService<IContentScanner>();
            var promptInjectionDetector = sp.GetService<IPromptInjectionDetector>() ?? new NullPromptInjectionDetector();
            var toolConfig = sp.GetRequiredService<ToolConfig>();
            var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
            var paths = sp.GetRequiredService<NetclawPaths>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MattermostThreadHistoryFetcher>();

            var gatewayClient = sp.GetRequiredService<IMattermostGatewayClient>();

            return new MattermostThreadHistoryFetcher(
                client,
                contentScanner,
                promptInjectionDetector,
                mattermostOptions,
                serverUrl,
                () => gatewayClient.BotUserId?.Value,
                toolConfig.AudienceProfiles,
                modelCapabilities,
                paths,
                logger);
        });
        services.AddSingleton<IReminderTargetResolver, MattermostReminderTargetResolver>();

        services.AddSingleton<IMattermostOutboundClient>(sp =>
        {
            var client = sp.GetRequiredService<MattermostClient>();
            return new MattermostNetOutboundClient(client);
        });

        services.AddKeyedSingleton<IChannel, MattermostChannel>(MattermostChannelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(MattermostChannelKey));
        services.AddSingleton<MattermostChannel>(sp =>
            (MattermostChannel)sp.GetRequiredKeyedService<IChannel>(MattermostChannelKey));

        // Channel-specific LLM tools: registered as IChannelTool singletons.
        // The gateway actor ref and default channel ID are resolved lazily via
        // MattermostChannel since they're not available until StartAsync completes.
        services.AddSingleton<SendMattermostMessageTool>(sp =>
        {
            var outbound = sp.GetRequiredService<IMattermostOutboundClient>();
            var channel = sp.GetRequiredService<MattermostChannel>();
            return new SendMattermostMessageTool(
                outbound,
                mattermostOptions,
                () => channel.DefaultChannelId,
                () => channel.Gateway);
        });
        services.AddSingleton<IChannelTool>(sp => sp.GetRequiredService<SendMattermostMessageTool>());

        services.AddSingleton<LookupMattermostUserTool>(sp =>
        {
            var client = sp.GetRequiredService<MattermostClient>();
            return new LookupMattermostUserTool(client, mattermostOptions);
        });
        services.AddSingleton<IChannelTool>(sp => sp.GetRequiredService<LookupMattermostUserTool>());

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(MattermostChannelKey));
    }
}
