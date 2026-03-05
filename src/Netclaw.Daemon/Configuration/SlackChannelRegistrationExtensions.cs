using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Slack.Tools;
using Netclaw.Tools;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;

namespace Netclaw.Daemon.Configuration;

public static class SlackChannelRegistrationExtensions
{
    private const string SlackChannelKey = "slack";

    public static void AddSlackChannelIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var slackOptions = configuration.GetSection("Slack").Get<SlackChannelOptions>() ?? new SlackChannelOptions();
        services.AddSingleton(slackOptions);

        if (!slackOptions.Enabled)
            return;

        if (slackOptions.BotToken is null || string.IsNullOrWhiteSpace(slackOptions.BotToken.Value))
            throw new InvalidOperationException("Slack is enabled but Slack:BotToken is not configured.");

        if (slackOptions.SocketMode && (slackOptions.AppToken is null || string.IsNullOrWhiteSpace(slackOptions.AppToken.Value)))
            throw new InvalidOperationException("Slack Socket Mode is enabled but Slack:AppToken is not configured.");

        services.AddHttpClient("slack-files");
        services.AddSingleton<ISlackReplyClient, SlackReplyClient>();
        services.AddSingleton<ISlackOutboundClient, SlackOutboundClient>();
        services.AddKeyedSingleton<IChannel, SlackChannel>(SlackChannelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));
        services.AddSingleton<SlackChannel>(sp =>
            (SlackChannel)sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));

        services.AddSlackNet(c =>
        {
            c.UseApiToken(slackOptions.BotToken!.Value);

            if (slackOptions.SocketMode)
                c.UseAppLevelToken(slackOptions.AppToken!.Value);

            c.RegisterEventHandler<MessageEvent, SlackChannel>();
            c.RegisterEventHandler<AppMention, SlackChannel>();
        });

        // Channel-specific LLM tools: registered as INetclawTool singletons.
        // The gateway actor ref and default channel ID are resolved lazily via
        // SlackChannel since they're not available until StartAsync completes.
        services.AddSingleton<SendSlackMessageTool>(sp =>
        {
            var outbound = sp.GetRequiredService<ISlackOutboundClient>();
            var channel = sp.GetRequiredService<SlackChannel>();
            return new SendSlackMessageTool(
                outbound,
                slackOptions,
                () => channel.DefaultChannelId,
                () => channel.Gateway);
        });
        services.AddSingleton<IChannelTool>(sp => sp.GetRequiredService<SendSlackMessageTool>());

        services.AddSingleton<LookupSlackUserTool>(sp =>
        {
            var slackApi = sp.GetRequiredService<SlackNet.ISlackApiClient>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            return new LookupSlackUserTool(slackApi.Users, slackOptions, timeProvider);
        });
        services.AddSingleton<IChannelTool>(sp => sp.GetRequiredService<LookupSlackUserTool>());

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));
    }
}
