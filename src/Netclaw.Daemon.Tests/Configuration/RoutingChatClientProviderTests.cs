// -----------------------------------------------------------------------
// <copyright file="RoutingChatClientProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.SelfHosted;
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
        var roles = new ModelRoleAssignments
        {
            Main = "main",
            Fallback = "fb"
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), roles);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });

        Assert.Equal(2, main.Count);
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Fallback }));
        // No distinct compaction model → compaction reuses the main candidate list.
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Compaction }));
    }

    [Fact]
    public void Main_has_one_candidate_when_no_fallback()
    {
        var roles = new ModelRoleAssignments { Main = "main" };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), roles);

        Assert.Single(router.Route(new ChatRoutingContext { Role = ModelRole.Main }));
    }

    [Fact]
    public void Compaction_is_a_distinct_single_pipeline_when_configured()
    {
        var roles = new ModelRoleAssignments
        {
            Main = "main",
            Compaction = "comp"
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), roles);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });
        var compaction = router.Route(new ChatRoutingContext { Role = ModelRole.Compaction });

        Assert.Single(compaction);
        Assert.NotSame(main, compaction);
    }
}

public sealed class NamedModelRuntimeRegistryTests
{
    [Fact]
    public void GetRequired_caches_one_runtime_per_case_insensitive_name()
    {
        var model = new ModelReference
        {
            Provider = "local",
            ModelId = "vision",
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text
        };
        var configuration = new ModelRuntimeConfiguration(
            new Dictionary<string, ModelReference>(StringComparer.OrdinalIgnoreCase)
            {
                ["vision"] = model
            },
            new ModelRoleAssignments { Main = "vision" },
            new ModelProxyAssignments { Image = "vision" });
        using var httpClient = new HttpClient();
        var providerFactory = new ProviderPluginFactory(
            new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["local"] = new ProviderEntry
                {
                    Type = "ollama",
                    Endpoint = "http://localhost:11434"
                }
            },
            [new OllamaProviderPlugin(new OllamaDescriptor(httpClient))]);
        var pipelineFactory = new PipelineChatClientFactory(
            providerFactory,
            new RetryPolicy(),
            NullLoggerFactory.Instance);
        var registry = new NamedModelRuntimeRegistry(configuration, pipelineFactory);

        var first = registry.GetRequired("vision");
        var second = registry.GetRequired("VISION");

        Assert.Same(first, second);
        Assert.Equal(ModelModality.Text | ModelModality.Image, first.Capabilities.InputModalities);
        Assert.Equal(ModelModality.Text, first.Capabilities.OutputModalities);
    }
}
