using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.App.Configuration;

/// <summary>
/// Resolves <see cref="IChatClient"/> instances by <see cref="ModelRole"/>
/// using a <see cref="ChatClientFactory"/> and <see cref="ModelSelection"/>.
/// Clients are created once at construction and reused for all requests.
/// </summary>
public sealed class NetclawChatClientProvider : IChatClientProvider
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
