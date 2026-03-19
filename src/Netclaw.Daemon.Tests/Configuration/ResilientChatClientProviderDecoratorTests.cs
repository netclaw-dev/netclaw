using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ResilientChatClientProviderDecoratorTests
{
    private static readonly RetryPolicy Policy = new()
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(10)
    };

    [Fact]
    public void MainRole_UsesFailover_WhenDistinctFallbackConfigured()
    {
        var rawMain = new FakeChatClient();
        var rawFallback = new FakeChatClient();
        var inner = new StubChatClientProvider(rawMain, rawFallback, rawMain);

        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "main", ModelId = "main-model" },
            Fallback = new ModelReference { Provider = "fallback", ModelId = "fallback-model" }
        };

        var decorated = new ResilientChatClientProviderDecorator(
            inner,
            Policy,
            models,
            NullLoggerFactory.Instance,
            NullNotificationSink.Instance);

        var main = decorated.GetClient(ModelRole.Main);
        var fallbackRole = decorated.GetClient(ModelRole.Fallback);
        var compaction = decorated.GetClient(ModelRole.Compaction);

        Assert.IsType<FailoverChatClient>(main);
        Assert.Same(main, fallbackRole);
        Assert.Same(main, compaction);
    }

    [Fact]
    public void MainRole_SkipsFailover_WhenFallbackResolvesToSameRawClient()
    {
        var rawMain = new FakeChatClient();
        var inner = new StubChatClientProvider(rawMain, rawMain, rawMain);

        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "main", ModelId = "main-model" },
            Fallback = new ModelReference { Provider = "fallback", ModelId = "fallback-model" }
        };

        var decorated = new ResilientChatClientProviderDecorator(
            inner,
            Policy,
            models,
            NullLoggerFactory.Instance,
            NullNotificationSink.Instance);

        var main = decorated.GetClient(ModelRole.Main);

        Assert.IsType<AlertingChatClientDecorator>(main);
        Assert.IsNotType<FailoverChatClient>(main);
        Assert.Same(main, decorated.GetClient(ModelRole.Fallback));
    }

    [Fact]
    public void CompactionRole_UsesSeparateDecoratedClient_WhenRawCompactionIsDistinct()
    {
        var rawMain = new FakeChatClient();
        var rawCompaction = new FakeChatClient();
        var inner = new StubChatClientProvider(rawMain, rawMain, rawCompaction);

        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "main", ModelId = "main-model" },
            Compaction = new ModelReference { Provider = "compaction", ModelId = "compaction-model" }
        };

        var decorated = new ResilientChatClientProviderDecorator(
            inner,
            Policy,
            models,
            NullLoggerFactory.Instance,
            NullNotificationSink.Instance);

        var main = decorated.GetClient(ModelRole.Main);
        var compaction = decorated.GetClient(ModelRole.Compaction);

        Assert.IsType<AlertingChatClientDecorator>(main);
        Assert.IsType<LoggingChatClient>(compaction);
        Assert.NotSame(main, compaction);
    }

    private sealed class StubChatClientProvider : IChatClientProvider
    {
        private readonly Microsoft.Extensions.AI.IChatClient _main;
        private readonly Microsoft.Extensions.AI.IChatClient _fallback;
        private readonly Microsoft.Extensions.AI.IChatClient _compaction;

        public StubChatClientProvider(
            Microsoft.Extensions.AI.IChatClient main,
            Microsoft.Extensions.AI.IChatClient fallback,
            Microsoft.Extensions.AI.IChatClient compaction)
        {
            _main = main;
            _fallback = fallback;
            _compaction = compaction;
        }

        public Microsoft.Extensions.AI.IChatClient GetClient(ModelRole role) => role switch
        {
            ModelRole.Fallback => _fallback,
            ModelRole.Compaction => _compaction,
            _ => _main
        };
    }
}
