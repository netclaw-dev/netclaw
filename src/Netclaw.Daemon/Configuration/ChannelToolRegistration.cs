using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Registers channel-specific LLM tools with the <see cref="ToolRegistry"/>
/// after the DI container is built. Channel tools are discovered dynamically
/// via the <see cref="IChannelTool"/> marker interface — only tools whose
/// channel adapter is enabled will be present in DI.
/// </summary>
internal static class ChannelToolRegistration
{
    public static void RegisterChannelTools(IServiceProvider services)
    {
        var registry = services.GetRequiredService<ToolRegistry>();

        foreach (var tool in services.GetServices<IChannelTool>())
        {
            registry.Register(tool);
        }
    }
}
