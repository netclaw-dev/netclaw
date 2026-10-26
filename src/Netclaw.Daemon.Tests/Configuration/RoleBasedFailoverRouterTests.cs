// -----------------------------------------------------------------------
// <copyright file="RoleBasedFailoverRouterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RoleBasedFailoverRouterTests
{
    [Fact]
    public void Main_has_two_candidates_when_fallback_configured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "p", ModelId = "main" },
            Fallback = new ModelReference { Provider = "p", ModelId = "fb" }
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });

        Assert.Equal(2, main.Count);
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Fallback }));
        // No distinct compaction model → compaction reuses the main candidate list.
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Compaction }));
    }

    [Fact]
    public void Main_has_one_candidate_when_no_fallback()
    {
        var models = new ModelSelection { Main = new ModelReference { Provider = "p", ModelId = "main" } };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        Assert.Single(router.Route(new ChatRoutingContext { Role = ModelRole.Main }));
    }

    [Fact]
    public void Compaction_is_a_distinct_single_pipeline_when_configured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "p", ModelId = "main" },
            Compaction = new ModelReference { Provider = "p", ModelId = "comp" }
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });
        var compaction = router.Route(new ChatRoutingContext { Role = ModelRole.Compaction });

        Assert.Single(compaction);
        Assert.NotSame(main, compaction);
    }

    // Two references name the same model when Provider and ModelId both match; ModelReference
    // has no separate definition ID in this version, and the other fields are tuning data.
    [Theory]
    [InlineData("fallback-set-compaction-distinct", "fb", "comp", "main,fb", "comp,fb")]
    [InlineData("fallback-set-compaction-equals-main", "fb", "main", "main,fb", "main,fb")]
    [InlineData("no-fallback-compaction-distinct", null, "comp", "main", "comp")]
    [InlineData("no-compaction", "fb", null, "main,fb", "main,fb")]
    [InlineData("no-compaction-no-fallback", null, null, "main", "main")]
    [InlineData("compaction-equals-fallback", "fb", "fb", "main,fb", "fb")]
    public void Candidate_lists_per_role(
        string row, string? fallbackId, string? compactionId, string expectedMain, string expectedCompaction)
    {
        var models = new ModelSelection
        {
            Main = Model("main"),
            Fallback = fallbackId is null ? null : Model(fallbackId),
            Compaction = compactionId is null ? null : Model(compactionId)
        };
        var modelIdByClient = new Dictionary<IChatClient, string>(ReferenceEqualityComparer.Instance);
        var router = new RoleBasedFailoverRouter(r =>
        {
            var client = new FakeChatClient();
            modelIdByClient[client] = r.ModelId;
            return client;
        }, models);

        string Ids(ModelRole role) => string.Join(",",
            router.Route(new ChatRoutingContext { Role = role }).Select(c => modelIdByClient[c]));

        Assert.True(expectedMain == Ids(ModelRole.Main), $"{row}: Main was [{Ids(ModelRole.Main)}]");
        Assert.True(expectedMain == Ids(ModelRole.Fallback), $"{row}: Fallback was [{Ids(ModelRole.Fallback)}]");
        Assert.True(expectedCompaction == Ids(ModelRole.Compaction), $"{row}: Compaction was [{Ids(ModelRole.Compaction)}]");
    }

    // A pipeline carries its definition's output limit and thinking level, so the router may reuse
    // a pipeline only for the same Provider, ModelId, and settings.
    [Theory]
    [InlineData("compaction_with_other_thinking_gets_own_pipeline", "Low", null, "High", null, false)]
    [InlineData("compaction_with_other_limit_gets_own_pipeline", null, 32_000, null, 4_096, false)]
    [InlineData("compaction_with_identical_settings_reuses_pipeline", "Low", 32_000, "Low", 32_000, true)]
    public void Settings_split_pipelines_per_role(
        string row, string? mainThinking, int? mainLimit, string? compactionThinking, int? compactionLimit, bool reuses)
    {
        var main = Model("same", mainThinking, mainLimit);
        var compaction = Model("same", compactionThinking, compactionLimit);
        var built = new Dictionary<IChatClient, ModelReference>(ReferenceEqualityComparer.Instance);
        var router = new RoleBasedFailoverRouter(r =>
        {
            var client = new FakeChatClient();
            built[client] = r;
            return client;
        }, new ModelSelection { Main = main, Compaction = compaction });

        var mainFirst = router.Route(new ChatRoutingContext { Role = ModelRole.Main })[0];
        var compactionFirst = router.Route(new ChatRoutingContext { Role = ModelRole.Compaction })[0];

        Assert.True(reuses == ReferenceEquals(mainFirst, compactionFirst), $"{row}: reuse was {!reuses}");
        Assert.Equal(compaction.Thinking, built[compactionFirst].Thinking);
        Assert.Equal(compaction.MaxOutputTokens, built[compactionFirst].MaxOutputTokens);
    }

    // Provider names are configuration keys, which the binder reads without case.
    [Fact]
    public void provider_name_case_shares_one_pipeline()
    {
        var calls = 0;
        var router = new RoleBasedFailoverRouter(_ =>
        {
            calls++;
            return new FakeChatClient();
        }, new ModelSelection
        {
            Main = new ModelReference { Provider = "Anthropic", ModelId = "claude-opus-5-5" },
            Compaction = new ModelReference { Provider = "anthropic", ModelId = "claude-opus-5-5" },
        });

        Assert.Same(
            router.Route(new ChatRoutingContext { Role = ModelRole.Main })[0],
            router.Route(new ChatRoutingContext { Role = ModelRole.Compaction })[0]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void fallback_with_other_thinking_gets_own_pipeline()
    {
        var built = new Dictionary<IChatClient, ModelReference>(ReferenceEqualityComparer.Instance);
        var router = new RoleBasedFailoverRouter(r =>
        {
            var client = new FakeChatClient();
            built[client] = r;
            return client;
        }, new ModelSelection { Main = Model("same", "Low", null), Fallback = Model("same", "Off", null) });

        var candidates = router.Route(new ChatRoutingContext { Role = ModelRole.Main });

        Assert.Equal([ThinkingLevel.Low, ThinkingLevel.Off], candidates.Select(c => built[c].Thinking));
    }

    private static ModelReference Model(string modelId) => new() { Provider = "p", ModelId = modelId };

    private static ModelReference Model(string modelId, string? thinking, int? limit) => new()
    {
        Provider = "p",
        ModelId = modelId,
        Thinking = thinking is null ? null : Enum.Parse<ThinkingLevel>(thinking),
        MaxOutputTokens = limit,
    };
}
