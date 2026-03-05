using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Tools;
using Netclaw.Channels.Slack.Tools;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Registers channel-specific LLM tools with the <see cref="ToolRegistry"/>
/// after the DI container is built. Channel tools are only present in DI
/// when their channel adapter is enabled.
/// </summary>
internal static class ChannelToolRegistration
{
    public static void RegisterChannelTools(IServiceProvider services)
    {
        var registry = services.GetRequiredService<ToolRegistry>();

        // Slack tools — only present when Slack adapter is enabled
        var sendTool = services.GetService<SendSlackMessageTool>();
        if (sendTool is not null)
            registry.Register(sendTool);

        var lookupTool = services.GetService<LookupSlackUserTool>();
        if (lookupTool is not null)
            registry.Register(lookupTool);
    }
}
