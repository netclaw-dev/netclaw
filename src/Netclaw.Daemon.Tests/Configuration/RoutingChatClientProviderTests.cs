// -----------------------------------------------------------------------
// <copyright file="RoutingChatClientProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RoutingChatClientProviderTests
{
    [Fact]
    public void GetClient_caches_per_role_and_shares_Main_and_Fallback()
    {
        var provider = new RoutingChatClientProvider(
            new StubRouter(), NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var main = provider.GetClient(ModelRole.Main);

        Assert.Same(main, provider.GetClient(ModelRole.Main));         // cached per role
        Assert.Same(main, provider.GetClient(ModelRole.Fallback));     // Fallback shares the Main routing client
        Assert.NotSame(main, provider.GetClient(ModelRole.Compaction));// Compaction is its own routing client
        Assert.Same(provider.GetClient(ModelRole.Compaction), provider.GetClient(ModelRole.Compaction));
    }

    private sealed class StubRouter : IChatClientRouter
    {
        private readonly IReadOnlyList<IChatClient> _candidates = [new FakeChatClient()];
        public IReadOnlyList<IChatClient> Route(ChatRoutingContext context) => _candidates;
    }
}

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
}
