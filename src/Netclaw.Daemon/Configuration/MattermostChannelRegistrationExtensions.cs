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

        // Token and server-URL validity are NOT checked here: an exception
        // thrown from this registration path aborts host construction and
        // crashes the daemon. A missing/invalid token or URL is handled as a
        // contained channel failure in MattermostChannel.StartAsync instead
        // (see issue #1033). The fallback values below are only ever
        // materialized for a misconfigured channel, which degrades before the
        // transport client is used.
        var serverUrl = string.IsNullOrWhiteSpace(mattermostOptions.ServerUrl)
            ? "https://mattermost.invalid"
            : mattermostOptions.ServerUrl;
        var botToken = mattermostOptions.BotToken?.Value ?? string.Empty;

        Uri? parsedServerUri = null;
        if (Uri.TryCreate(serverUrl.TrimEnd('/'), UriKind.Absolute, out var candidate))
            parsedServerUri = candidate;

        services.AddSingleton(_ => new MattermostClient(serverUrl, botToken));

        services.AddHttpClient("mattermost-files", client =>
        {
            if (!string.IsNullOrEmpty(botToken))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", botToken);
        });
        services.AddHttpClient("mattermost-api", client =>
        {
            if (parsedServerUri is not null)
                client.BaseAddress = parsedServerUri;
            if (!string.IsNullOrEmpty(botToken))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", botToken);
        });
        if (!string.IsNullOrEmpty(mattermostOptions.CallbackUrl))
            services.AddSingleton(new MattermostCallbackActionStore(TimeProvider.System));

        services.AddSingleton<IMattermostGatewayClient, MattermostNetGatewayClient>();
        services.AddSingleton<IMattermostReplyClient>(sp =>
        {
            var client = sp.GetRequiredService<MattermostClient>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("mattermost-api");
            return new MattermostNetReplyClient(client, httpClient);
        });
        services.AddSingleton<IThreadHistoryFetcher>(sp =>
        {
            var client = sp.GetRequiredService<MattermostClient>();
            var contentScanner = sp.GetRequiredService<IContentScanner>();
            var toolConfig = sp.GetRequiredService<ToolConfig>();
            var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
            var paths = sp.GetRequiredService<NetclawPaths>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MattermostThreadHistoryFetcher>();

            var gatewayClient = sp.GetRequiredService<IMattermostGatewayClient>();

            return new MattermostThreadHistoryFetcher(
                client,
                contentScanner,
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
