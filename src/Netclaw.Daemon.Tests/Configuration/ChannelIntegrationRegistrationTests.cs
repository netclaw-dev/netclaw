// -----------------------------------------------------------------------
// <copyright file="ChannelIntegrationRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Akka.Actor;
using Akka.Streams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Tests.Services;
using Netclaw.Security;
using Xunit;
using ChannelType = Netclaw.Actors.Channels.ChannelType;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ChannelIntegrationRegistrationTests
{
    [Fact]
    public void Invalid_mattermost_server_url_does_not_throw_during_registration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(new RecordingHandler(System.Net.HttpStatusCode.OK)));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mattermost:Enabled"] = "true",
                ["Mattermost:ServerUrl"] = "://not-a-uri",
                ["Mattermost:BotToken"] = "fake-token",
                ["Mattermost:AllowedChannelIds:0"] = "channel-1"
            })
            .Build();

        var ex = Record.Exception(() => services.AddChannelIntegrations(configuration));

        Assert.Null(ex);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MattermostChannelOptions>();
        Assert.Equal("://not-a-uri", options.ServerUrl);
        Assert.Equal("fake-token", options.BotToken!.Value);
    }

    [Fact]
    public void Thread_history_fetchers_resolve_keyed_per_channel()
    {
        var services = BuildChannelServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "true"
        });

        // The cross-wiring defect this guards against: an UNKEYED
        // IThreadHistoryFetcher registration makes every channel resolve the
        // LAST registered fetcher (Discord here), so Slack thread rehydration
        // silently backfills empty in multi-channel hosts.
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IThreadHistoryFetcher) && descriptor.ServiceKey is null);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<SlackThreadHistoryFetcher>(
            provider.GetRequiredKeyedService<IThreadHistoryFetcher>("slack"));
        Assert.IsType<DiscordThreadHistoryFetcher>(
            provider.GetRequiredKeyedService<IThreadHistoryFetcher>("discord"));
    }

    [Fact]
    public async Task Slack_resolver_allows_runtime_resolved_default_channel_id()
    {
        var system = ActorSystem.Create("slack-resolver-wiring");
        try
        {
            var services = BuildChannelServices(new Dictionary<string, string?>
            {
                // Only the NAME is configured: the channel ID materializes at
                // runtime when SlackChannel.StartAsync resolves it, so a
                // config-only accessor would never see it.
                ["Slack:Enabled"] = "true",
                ["Slack:DefaultChannelName"] = "ops"
            });
            services.AddSingleton(system);
            services.AddSingleton<ISessionPipeline>(new NoopSessionPipeline());
            services.AddSingleton(new SessionIngressGate());
            services.AddSingleton<IPromptInjectionDetector>(new SafePromptInjectionDetector());
            services.AddSingleton<IOperationalNotificationSink>(NullNotificationSink.Instance);

            await using var provider = services.BuildServiceProvider();
            var channel = provider.GetRequiredService<SlackChannel>();

            // Arrange the runtime-resolved default channel ID without a live
            // Slack connection (StartAsync owns this field in production).
            typeof(SlackChannel)
                .GetField("_defaultChannelId", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(channel, new SlackChannelId("C0RUNTIME99"));

            var resolver = provider.GetRequiredService<SlackTargetResolver>();
            var result = await resolver.ResolveAsync(
                new ChannelAddressResolutionRequest(
                    ChannelDescriptorKey.FromChannelType(ChannelType.Slack),
                    ChannelAddressKind.Destination,
                    "C0RUNTIME99"),
                TestContext.Current.CancellationToken);

            // Fails when the resolver is wired to a config-only accessor: the
            // config has no DefaultChannelId, so the ACL denies the ID.
            Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static ServiceCollection BuildChannelServices(IReadOnlyDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IContentScanner>(new NullContentScanner());
        services.AddSingleton(new ToolConfig());
        services.AddSingleton(new ModelCapabilities());
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        services.AddSingleton(paths);
        services.AddSingleton<ISessionStorageResolver>(new TestSessionStorageResolver(paths));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        services.AddChannelIntegrations(configuration);
        return services;
    }

    private sealed class NoopSessionPipeline : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }

    private sealed class SafePromptInjectionDetector : IPromptInjectionDetector
    {
        public Task<PromptInjectionResult> DetectAsync(
            string text,
            string sourceContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PromptInjectionResult.Safe());
    }
}
