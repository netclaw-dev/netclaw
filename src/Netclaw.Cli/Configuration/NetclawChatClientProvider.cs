using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Cli.Configuration;

/// <summary>
/// Transitional: duplicated from Netclaw.Daemon. Removed in Task 1.28
/// when CLI connects to daemon via SignalR instead of running in-process.
/// </summary>
internal sealed class NetclawChatClientProvider : IChatClientProvider
{
    private readonly IChatClient _main;
    private readonly IChatClient? _fallback;
    private readonly IChatClient? _compaction;

    public NetclawChatClientProvider(ChatClientFactory factory, ModelSelection models)
    {
        _main = factory.Create(models.Main);
        _fallback = models.Fallback is not null
            ? factory.Create(models.Fallback) : null;
        _compaction = models.Compaction is not null
            ? factory.Create(models.Compaction) : null;
    }

    public IChatClient GetClient(ModelRole role) => role switch
    {
        ModelRole.Fallback => _fallback ?? _main,
        ModelRole.Compaction => _compaction ?? _main,
        _ => _main
    };
}
