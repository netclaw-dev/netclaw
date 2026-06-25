// -----------------------------------------------------------------------
// <copyright file="ChannelRegistryRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Tools;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Slack.Tools;
using Netclaw.Daemon.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ChannelRegistryRegistrationTests
{
    [Fact]
    public void Registry_enumerates_output_capable_channels_only()
    {
        var descriptors = BuildDescriptors(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Slack:AllowDirectMessages"] = "true",
            ["Discord:Enabled"] = "true",
            ["Discord:AllowDirectMessages"] = "true",
            ["Mattermost:Enabled"] = "true",
            ["Mattermost:AllowDirectMessages"] = "true"
        });

        Assert.Equal(
            new[] { "discord", "mattermost", "slack", "tui" },
            descriptors.Keys.Order(StringComparer.Ordinal));

        Assert.DoesNotContain("headless", descriptors.Keys);
        Assert.DoesNotContain("signalr", descriptors.Keys);
        Assert.DoesNotContain("reminder", descriptors.Keys);
        Assert.DoesNotContain("webhook", descriptors.Keys);

        Assert.Equal(ChannelKind.RemoteChat, descriptors["slack"].Kind);
        Assert.Equal(ChannelKind.RemoteChat, descriptors["discord"].Kind);
        Assert.Equal(ChannelKind.RemoteChat, descriptors["mattermost"].Kind);
        Assert.Equal(ChannelKind.LocalInteractiveClient, descriptors["tui"].Kind);

        Assert.Equal(ChannelType.Tui, descriptors["tui"].ChannelType);
        Assert.NotEqual(ChannelType.SignalR, descriptors["tui"].ChannelType);

        foreach (var key in new[] { "slack", "discord", "mattermost" })
        {
            var descriptor = descriptors[key];
            Assert.True(descriptor.IsEnabled);
            Assert.True(descriptor.Capabilities.HasFlag(ChannelCapabilities.ReceiveMessages));
            Assert.True(descriptor.Capabilities.HasFlag(ChannelCapabilities.SendMessages));
            Assert.True(descriptor.Capabilities.HasFlag(ChannelCapabilities.RuntimeHealth));
            Assert.Contains(ChannelAddressKind.Destination, descriptor.AddressKinds);
            Assert.Contains(ChannelOutputEffectKind.TextMessage, descriptor.SupportedOutputEffects);
            Assert.Contains(ChannelToolIntentKind.SendMessage, descriptor.ToolIntents);
        }

        Assert.True(descriptors["slack"].Capabilities.HasFlag(ChannelCapabilities.DirectMessages));
        Assert.True(descriptors["mattermost"].Capabilities.HasFlag(ChannelCapabilities.DirectMessages));
        Assert.True(descriptors["discord"].Capabilities.HasFlag(ChannelCapabilities.DirectMessages));

        Assert.Contains(ChannelAddressKind.DirectMessage, descriptors["slack"].AddressKinds);
        Assert.Contains(ChannelAddressKind.DirectMessage, descriptors["mattermost"].AddressKinds);
        Assert.Contains(ChannelAddressKind.DirectMessage, descriptors["discord"].AddressKinds);

        Assert.True(descriptors["slack"].Capabilities.HasFlag(ChannelCapabilities.FileEgress));
        Assert.True(descriptors["discord"].Capabilities.HasFlag(ChannelCapabilities.FileEgress));
        Assert.True(descriptors["mattermost"].Capabilities.HasFlag(ChannelCapabilities.FileEgress));

        Assert.Contains(ChannelOutputEffectKind.FileAttachment, descriptors["slack"].SupportedOutputEffects);
        Assert.Contains(ChannelOutputEffectKind.FileAttachment, descriptors["discord"].SupportedOutputEffects);
        Assert.Contains(ChannelOutputEffectKind.FileAttachment, descriptors["mattermost"].SupportedOutputEffects);

        Assert.Contains(ChannelOutputEffectKind.ProcessingIndicator, descriptors["discord"].SupportedOutputEffects);
        Assert.DoesNotContain(ChannelOutputEffectKind.ProcessingIndicator, descriptors["slack"].SupportedOutputEffects);
        Assert.DoesNotContain(ChannelOutputEffectKind.ProcessingIndicator, descriptors["mattermost"].SupportedOutputEffects);
    }

    [Fact]
    public void Disabled_remote_channels_still_have_disabled_descriptors()
    {
        var descriptors = BuildDescriptors(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.False(descriptors["slack"].IsEnabled);
        Assert.False(descriptors["discord"].IsEnabled);
        Assert.False(descriptors["mattermost"].IsEnabled);
        Assert.True(descriptors["tui"].IsEnabled);
    }

    [Fact]
    public void Disabled_remote_channels_do_not_register_channel_tools()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannelTool));
        Assert.False(IsRegistered<SendChannelMessageTool>(services));
        Assert.False(IsRegistered<LookupChannelUserTool>(services));
        Assert.False(IsRegistered<LookupChannelDestinationTool>(services));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannelOutboundClient));
        Assert.False(IsRegistered<SlackProactiveOutboundClient>(services));
        Assert.False(IsRegistered<LookupSlackUserTool>(services));
        Assert.False(IsRegistered<DiscordProactiveOutboundClient>(services));
        Assert.False(IsRegistered<MattermostProactiveOutboundClient>(services));
        Assert.False(IsRegistered<LookupMattermostUserTool>(services));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannelAddressResolver));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannelOutputRenderer));
        Assert.False(IsRegistered<SlackTargetResolver>(services));
        Assert.False(IsRegistered<DiscordAddressResolver>(services));
        Assert.False(IsRegistered<MattermostDestinationAddressResolver>(services));
    }

    [Fact]
    public void Enabled_remote_channels_register_expected_channel_tools()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "true"
        });

        // N enabled channels produce exactly ONE registration of each generic
        // tool plus ONE IChannelTool forward each (3 channels → 3 forwards
        // total, not 9).
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelTool)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(SendChannelMessageTool)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(LookupChannelUserTool)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(LookupChannelDestinationTool)));
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelOutboundClient)));
        Assert.True(IsRegistered<SlackProactiveOutboundClient>(services));
        Assert.True(IsRegistered<LookupSlackUserTool>(services));
        Assert.False(typeof(IChannelTool).IsAssignableFrom(typeof(LookupSlackUserTool)));
        Assert.True(IsRegistered<DiscordProactiveOutboundClient>(services));
        Assert.True(IsRegistered<MattermostProactiveOutboundClient>(services));
        Assert.True(IsRegistered<LookupMattermostUserTool>(services));
        Assert.False(typeof(IChannelTool).IsAssignableFrom(typeof(LookupMattermostUserTool)));
        Assert.True(IsRegistered<DiscordProcessingOutputRenderer>(services));
    }

    [Fact]
    public void Enabled_remote_channels_register_expected_address_resolvers()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "true"
        });

        Assert.Equal(5, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelAddressResolver)));
        Assert.True(IsRegistered<SlackTargetResolver>(services));
        Assert.True(IsRegistered<LookupSlackUserTool>(services));
        Assert.True(IsRegistered<DiscordAddressResolver>(services));
        Assert.True(IsRegistered<MattermostDestinationAddressResolver>(services));
        Assert.True(IsRegistered<LookupMattermostUserTool>(services));
    }

    [Fact]
    public void Enabled_channel_tool_intents_match_registered_tool_services()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "true"
        };
        var services = BuildServices(settings);

        using var provider = BuildProvider(settings);
        var descriptors = provider.GetRequiredService<IChannelRegistry>()
            .ListChannels()
            .ToDictionary(descriptor => descriptor.Key.Value, StringComparer.Ordinal);

        AssertToolIntents(
            descriptors["slack"],
            services,
            new ChannelToolExpectation(ChannelToolIntentKind.SendMessage, typeof(SendChannelMessageTool), null),
            new ChannelToolExpectation(ChannelToolIntentKind.LookupUser, typeof(LookupChannelUserTool), null));
        AssertToolIntents(
            descriptors["discord"],
            services,
            new ChannelToolExpectation(ChannelToolIntentKind.SendMessage, typeof(SendChannelMessageTool), null),
            new ChannelToolExpectation(ChannelToolIntentKind.LookupUser, typeof(LookupChannelUserTool), null));
        AssertToolIntents(
            descriptors["mattermost"],
            services,
            new ChannelToolExpectation(ChannelToolIntentKind.SendMessage, typeof(SendChannelMessageTool), null),
            new ChannelToolExpectation(ChannelToolIntentKind.LookupUser, typeof(LookupChannelUserTool), null));
    }

    [Fact]
    public async Task Registry_returns_runtime_snapshots_for_registered_descriptors()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        var registry = provider.GetRequiredService<IChannelRegistry>();

        var slack = await registry.GetSnapshotAsync(
            ChannelDescriptorKey.FromChannelType(ChannelType.Slack),
            TestContext.Current.CancellationToken);
        Assert.False(slack.IsEnabled);
        Assert.Equal(ChannelHealthStatus.Degraded, slack.Health);
        Assert.Equal("Slack connector is disabled in configuration.", slack.HealthDetail);

        var tui = await registry.GetSnapshotAsync(
            ChannelDescriptorKey.FromChannelType(ChannelType.Tui),
            TestContext.Current.CancellationToken);
        Assert.True(tui.IsEnabled);
        Assert.Equal(ChannelHealthStatus.Healthy, tui.Health);
        Assert.True(tui.IsReady);
    }

    [Fact]
    public void Registry_fails_loudly_on_duplicate_descriptor_keys()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = new ChannelDescriptor(
            key,
            ChannelType.Slack,
            ChannelKind.RemoteChat,
            "Slack",
            IsEnabled: true,
            ChannelCapabilities.SendMessages,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());

        var providers = new IChannelDescriptorProvider[]
        {
            new StaticChannelDescriptorProvider(descriptor),
            new StaticChannelDescriptorProvider(descriptor)
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ChannelRegistry(providers, Array.Empty<IChannelRuntimeSnapshotProvider>()));

        Assert.Contains("Duplicate channel descriptor key 'slack'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_fails_loudly_on_duplicate_address_resolvers()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = BuildDescriptor(key, ChannelType.Slack, ChannelAddressKind.User);
        var resolver = new TestAddressResolver(key, ChannelAddressKind.User);

        var providers = new[] { new StaticChannelDescriptorProvider(descriptor) };
        var resolvers = new[] { resolver, resolver };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ChannelRegistry(providers, Array.Empty<IChannelRuntimeSnapshotProvider>(), resolvers));

        Assert.Contains(
            "Duplicate channel address resolver key 'slack' for address kind 'User' registered.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_fails_loudly_on_duplicate_outbound_clients()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = BuildDescriptor(key, ChannelType.Slack, ChannelAddressKind.Destination);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ChannelRegistry(
                [new StaticChannelDescriptorProvider(descriptor)],
                Array.Empty<IChannelRuntimeSnapshotProvider>(),
                outboundClients: [new TestOutboundClient(key), new TestOutboundClient(key)]));

        Assert.Contains("Duplicate channel outbound client key 'slack'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_resolves_each_enabled_channel_outbound_client_by_key()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "true"
        });

        var registry = provider.GetRequiredService<IChannelRegistry>();

        Assert.IsType<SlackProactiveOutboundClient>(
            registry.GetOutboundClient(ChannelDescriptorKey.FromChannelType(ChannelType.Slack)));
        Assert.IsType<DiscordProactiveOutboundClient>(
            registry.GetOutboundClient(ChannelDescriptorKey.FromChannelType(ChannelType.Discord)));
        Assert.IsType<MattermostProactiveOutboundClient>(
            registry.GetOutboundClient(ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost)));
    }

    [Fact]
    public void Registry_fails_loudly_when_address_kind_is_not_supported()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);
        var descriptor = BuildDescriptor(key, ChannelType.Discord, ChannelAddressKind.Destination);
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            Array.Empty<IChannelRuntimeSnapshotProvider>(),
            Array.Empty<IChannelAddressResolver>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.GetResolver(key, ChannelAddressKind.DirectMessage));

        Assert.Contains("Channel 'discord' does not support address kind 'DirectMessage'.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_fails_loudly_when_supported_address_kind_has_no_resolver()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);
        var descriptor = BuildDescriptor(key, ChannelType.Mattermost, ChannelAddressKind.User);
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            Array.Empty<IChannelRuntimeSnapshotProvider>(),
            Array.Empty<IChannelAddressResolver>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.GetResolver(key, ChannelAddressKind.User));

        Assert.Contains(
            "No channel address resolver is registered for key 'mattermost' and address kind 'User'.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_routes_resolution_requests_to_selected_channel_resolver()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = BuildDescriptor(key, ChannelType.Slack, ChannelAddressKind.User, ChannelAddressKind.Destination);
        var userAddress = new ResolvedChannelAddress(key, ChannelAddressKind.User, "U123", "Jennifer Stannard");
        var destinationAddress = new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C123", "#general");
        var resolver = new TestAddressResolver(key, ChannelAddressKind.User, ChannelAddressKind.Destination)
        {
            Result = ChannelAddressResolutionResult.Ambiguous([userAddress, destinationAddress], "Multiple matches.")
        };
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            Array.Empty<IChannelRuntimeSnapshotProvider>(),
            [resolver]);
        var request = new ChannelAddressResolutionRequest(key, ChannelAddressKind.User, "jennifer", requireSingleMatch: false);

        var result = await registry.ResolveAddressAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(resolver.Result, result);
        Assert.Same(request, resolver.Request);
    }

    [Fact]
    public async Task Registry_routes_supported_optional_output_effect_to_registered_renderer()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);
        var descriptor = BuildDescriptor(key, ChannelType.Discord, ChannelAddressKind.Destination) with
        {
            SupportedOutputEffects = new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.ProcessingIndicator
            }
        };
        var renderer = new TestOutputRenderer(key);
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            [],
            outputRenderers: [renderer]);
        var request = BuildProcessingRenderRequest(key);

        var result = await registry.RenderOutputAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelOutputRenderStatus.Rendered, result.Status);
        Assert.Same(request, renderer.Request);
    }

    [Fact]
    public async Task Registry_ignores_unsupported_optional_output_effect()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);
        var descriptor = BuildDescriptor(key, ChannelType.Mattermost, ChannelAddressKind.Destination);
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            []);
        var request = BuildProcessingRenderRequest(key);

        var result = await registry.RenderOutputAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelOutputRenderStatus.IgnoredUnsupported, result.Status);
        Assert.Contains("does not support output effect 'ProcessingIndicator'", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_fails_loudly_for_unsupported_required_output_effect()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = BuildDescriptor(key, ChannelType.Slack, ChannelAddressKind.Destination);
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            []);
        var request = BuildProcessingRenderRequest(key) with
        {
            Requirement = ChannelOutputRequirement.Required
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.RenderOutputAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("Required output effects cannot be ignored.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_fails_loudly_when_supported_output_effect_has_no_renderer()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);
        var descriptor = BuildDescriptor(key, ChannelType.Discord, ChannelAddressKind.Destination) with
        {
            SupportedOutputEffects = new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.ProcessingIndicator
            }
        };
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            []);
        var request = BuildProcessingRenderRequest(key);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.RenderOutputAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains(
            "declares support for output effect 'ProcessingIndicator' but no channel output renderer is registered",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discord_processing_renderer_triggers_typing_for_processing_start()
    {
        var replyClient = new RecordingDiscordReplyClient();
        var renderer = new DiscordProcessingOutputRenderer(replyClient);

        await renderer.RenderAsync(
            BuildProcessingRenderRequest(ChannelDescriptorKey.FromChannelType(ChannelType.Discord)),
            TestContext.Current.CancellationToken);

        var channelId = Assert.Single(replyClient.TypingTriggers);
        Assert.Equal("channel-1", channelId.Value);
    }

    private static IReadOnlyDictionary<string, ChannelDescriptor> BuildDescriptors(
        IReadOnlyDictionary<string, string?> settings)
    {
        using var provider = BuildProvider(settings);
        return provider.GetRequiredService<IChannelRegistry>()
            .ListChannels()
            .ToDictionary(descriptor => descriptor.Key.Value, StringComparer.Ordinal);
    }

    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> settings)
    {
        return BuildServices(settings).BuildServiceProvider();
    }

    private static ServiceCollection BuildServices(IReadOnlyDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddChannelRegistry();
        services.AddTuiChannelDescriptor();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        // The generic channel tools are registered by the builder inside
        // AddChannelIntegrations when at least one remote channel is enabled.
        services.AddChannelIntegrations(configuration);

        return services;
    }

    private static bool IsRegistered<T>(IServiceCollection services)
    {
        return services.Any(descriptor => descriptor.ServiceType == typeof(T));
    }

    private static ChannelOutputRenderRequest BuildProcessingRenderRequest(ChannelDescriptorKey key)
    {
        var target = new ChannelDeliveryTarget(
            key,
            new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "channel-1", "channel-1"));

        return new ChannelOutputRenderRequest(
            target,
            new ProcessingStateOutput(true)
            {
                SessionId = new SessionId("session-1")
            },
            ChannelOutputEffectKind.ProcessingIndicator);
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelDescriptorKey key,
        ChannelType channelType,
        params ChannelAddressKind[] addressKinds)
    {
        return new ChannelDescriptor(
            key,
            channelType,
            ChannelKind.RemoteChat,
            channelType.ToString(),
            IsEnabled: true,
            ChannelCapabilities.SendMessages,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(addressKinds),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }

    private static void AssertToolIntents(
        ChannelDescriptor descriptor,
        IServiceCollection services,
        params ChannelToolExpectation[] expectedTools)
    {
        Assert.True(descriptor.IsEnabled);

        Assert.Equal(
            expectedTools.Select(tool => tool.Intent).Order().ToArray(),
            descriptor.ToolIntents.Order().ToArray());

        foreach (var expectedTool in expectedTools)
        {
            Assert.Contains(services, serviceDescriptor => serviceDescriptor.ServiceType == expectedTool.ToolType);

            if (expectedTool.ToolName is null)
                continue;

            var attribute = Assert.Single(expectedTool.ToolType.GetCustomAttributes(
                typeof(NetclawToolAttribute), inherit: false));
            Assert.Equal(expectedTool.ToolName, ((NetclawToolAttribute)attribute).Name);
        }
    }

    private sealed record ChannelToolExpectation(
        ChannelToolIntentKind Intent,
        Type ToolType,
        string? ToolName);

    private sealed class TestAddressResolver(
        ChannelDescriptorKey key,
        params ChannelAddressKind[] addressKinds) : IChannelAddressResolver
    {
        public ChannelDescriptorKey Key { get; } = key;

        public IReadOnlySet<ChannelAddressKind> AddressKinds { get; } = new HashSet<ChannelAddressKind>(addressKinds);

        public ChannelAddressResolutionRequest? Request { get; private set; }

        public ChannelAddressResolutionResult Result { get; init; } = ChannelAddressResolutionResult.NotFound();

        public ValueTask<ChannelAddressResolutionResult> ResolveAsync(
            ChannelAddressResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class TestOutboundClient(ChannelDescriptorKey key) : IChannelOutboundClient
    {
        public ChannelDescriptorKey Key { get; } = key;

        public Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
            => Task.FromResult($"sent via {Key.Value}");
    }

    private sealed class TestOutputRenderer(ChannelDescriptorKey key) : IChannelOutputRenderer
    {
        public ChannelDescriptorKey Key { get; } = key;

        public ChannelOutputRenderRequest? Request { get; private set; }

        public ValueTask RenderAsync(
            ChannelOutputRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDiscordReplyClient : IDiscordReplyClient
    {
        public List<DiscordReplyChannelId> TypingTriggers { get; } = [];

        public Task<DiscordPostResult> PostReplyAsync(
            DiscordPostMessage message,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DiscordPostResult.Default);

        public Task SetThreadNameAsync(
            DiscordReplyChannelId threadChannelId,
            string name,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateMessageAsync(
            DiscordReplyChannelId channelId,
            DiscordMessageId messageId,
            string text,
            bool removeComponents = false,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TriggerTypingAsync(DiscordReplyChannelId channelId, CancellationToken cancellationToken = default)
        {
            TypingTriggers.Add(channelId);
            return Task.CompletedTask;
        }

        public Task<DiscordMessageId?> UploadFileAsync(DiscordFileUpload upload, CancellationToken cancellationToken = default)
            => Task.FromResult<DiscordMessageId?>(new DiscordMessageId("file-1"));
    }
}
