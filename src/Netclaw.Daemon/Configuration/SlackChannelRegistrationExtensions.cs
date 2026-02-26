using Netclaw.Channels;
using Netclaw.Channels.Slack;
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

        if (string.IsNullOrWhiteSpace(slackOptions.BotToken))
            throw new InvalidOperationException("Slack is enabled but Slack:BotToken is not configured.");

        if (slackOptions.SocketMode && string.IsNullOrWhiteSpace(slackOptions.AppToken))
            throw new InvalidOperationException("Slack Socket Mode is enabled but Slack:AppToken is not configured.");

        services.AddSingleton<ISlackReplyClient, SlackReplyClient>();
        services.AddKeyedSingleton<IChannel, SlackChannel>(SlackChannelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));
        services.AddSingleton<SlackChannel>(sp =>
            (SlackChannel)sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));

        services.AddSlackNet(c =>
        {
            c.UseApiToken(slackOptions.BotToken!);

            if (slackOptions.SocketMode)
                c.UseAppLevelToken(slackOptions.AppToken!);

            c.RegisterEventHandler<MessageEvent, SlackChannel>();
            c.RegisterEventHandler<AppMention, SlackChannel>();
        });

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));
    }
}
