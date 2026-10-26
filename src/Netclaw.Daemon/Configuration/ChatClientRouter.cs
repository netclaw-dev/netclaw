// -----------------------------------------------------------------------
// <copyright file="ChatClientRouter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Selects which composed chat-client pipeline(s) to invoke for a given routing
/// context. Returns candidates in priority order; <see cref="RoutingChatClient"/>
/// walks them, so failover is "the candidate list has more than one entry" rather
/// than a bespoke decorator. The router is the seam where the future per-role /
/// per-agent / per-provider routing tracked in netclaw-dev/netclaw#648 slots in as
/// a different policy.
/// </summary>
public interface IChatClientRouter
{
    /// <summary>
    /// Returns the composed pipelines to try, in order, for this context. Never empty.
    /// </summary>
    IReadOnlyList<IChatClient> Route(ChatRoutingContext context);
}

/// <summary>
/// Today's policy: route by <see cref="ModelRole"/> with primary→fallback failover.
/// Builds one pipeline per distinct model via <see cref="PipelineChatClientFactory"/>
/// and maps each role to an ordered candidate list. Two references share a pipeline
/// when Provider, ModelId, ContextWindow, MaxOutputTokens, and Thinking all match.
/// <list type="bullet">
/// <item>Main / Fallback → <c>[main]</c>, plus <c>fallback</c> when a different
/// fallback model is configured.</item>
/// <item>Compaction → <c>[compaction]</c>, plus <c>fallback</c> when a fallback is
/// configured and is not the compaction model. When Compaction is the same model as
/// Main, or is not configured, it reuses the main candidate list.</item>
/// </list>
/// A pipeline is never built twice for one model, and the fallback never appears twice.
/// </summary>
public sealed class RoleBasedFailoverRouter : IChatClientRouter
{
    private readonly IReadOnlyList<IChatClient> _mainCandidates;
    private readonly IReadOnlyList<IChatClient> _compactionCandidates;

    public RoleBasedFailoverRouter(PipelineChatClientFactory factory, ModelSelection models)
        : this(factory.Create, models)
    {
    }

    // Test seam: build candidates from any create function, independent of the provider
    // plumbing PipelineChatClientFactory needs.
    internal RoleBasedFailoverRouter(Func<ModelReference, IChatClient> create, ModelSelection models)
    {
        var built = new Dictionary<PipelineKey, IChatClient>();

        IChatClient Pipeline(ModelReference model)
        {
            var key = PipelineKey.Of(model);
            if (!built.TryGetValue(key, out var client))
                built[key] = client = create(model);
            return client;
        }

        // Every role's list ends with the fallback, so a refused connection on any
        // role's primary still reaches it.
        IReadOnlyList<IChatClient> Candidates(ModelReference primary)
        {
            var first = Pipeline(primary);
            if (models.Fallback is null)
                return [first];

            var fallback = Pipeline(models.Fallback);
            return ReferenceEquals(first, fallback) ? [first] : [first, fallback];
        }

        _mainCandidates = Candidates(models.Main);

        _compactionCandidates = models.Compaction is not null
            && !IsSameModel(models.Compaction, models.Main)
                ? Candidates(models.Compaction)
                : _mainCandidates;
    }

    private static bool IsSameModel(ModelReference a, ModelReference b) => PipelineKey.Of(a) == PipelineKey.Of(b);

    // A pipeline carries the definition's request settings (the context window of the clamp, the
    // output limit, and the thinking level), so two definitions share a pipeline only when they
    // name the same model with the same settings.
    private readonly record struct PipelineKey(
        string Provider, string ModelId, int? ContextWindow, int? MaxOutputTokens, ThinkingLevel? Thinking)
    {
        // Provider names are configuration keys, which the binder reads without case.
        public static PipelineKey Of(ModelReference model) =>
            new(model.Provider.ToUpperInvariant(), model.ModelId, model.ContextWindow, model.MaxOutputTokens, model.Thinking);
    }

    public IReadOnlyList<IChatClient> Route(ChatRoutingContext context) => context.Role switch
    {
        ModelRole.Compaction => _compactionCandidates,
        // Main and Fallback both resolve to the main candidate list (which already
        // contains the fallback when configured), matching the prior provider contract.
        _ => _mainCandidates
    };
}
