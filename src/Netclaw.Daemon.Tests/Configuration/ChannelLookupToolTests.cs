// -----------------------------------------------------------------------
// <copyright file="ChannelLookupToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Daemon.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ChannelLookupToolTests
{
    [Fact]
    public void Registration_skips_generic_lookup_tools_when_remote_channels_are_disabled()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.False(IsRegistered<LookupChannelUserTool>(services));
        Assert.False(IsRegistered<LookupChannelDestinationTool>(services));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannelTool));
    }

    [Fact]
    public void Registration_adds_user_and_destination_for_discord_only_configuration()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.True(IsRegistered<LookupChannelUserTool>(services));
        Assert.True(IsRegistered<LookupChannelDestinationTool>(services));
        // Three IChannelTool forwards: the two lookup tools plus the generic
        // send tool, all registered once by the builder.
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelTool)));
    }

    [Fact]
    public void Registration_adds_user_and_destination_for_user_lookup_channels()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.True(IsRegistered<LookupChannelUserTool>(services));
        Assert.True(IsRegistered<LookupChannelDestinationTool>(services));
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelTool)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(LookupChannelUserTool)));
        Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(LookupChannelDestinationTool)));
    }

    [Fact]
    public void User_lookup_schema_enumerates_enabled_user_channels_only()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.User),
            BuildDescriptor(ChannelType.Mattermost, isEnabled: false, ChannelAddressKind.User),
            BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelAddressKind.Destination));
        var tool = new LookupChannelUserTool(registry);

        var keys = ReadChannelKeyEnum(tool.ParameterSchema);

        Assert.Equal(["slack"], keys);
    }

    [Fact]
    public void Destination_lookup_schema_enumerates_enabled_destination_channels()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Mattermost, isEnabled: true, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Tui, isEnabled: true, ChannelAddressKind.LocalSession));
        var tool = new LookupChannelDestinationTool(registry);

        var keys = ReadChannelKeyEnum(tool.ParameterSchema);

        Assert.Equal(["discord", "mattermost", "slack"], keys);
    }

    [Fact]
    public async Task User_lookup_routes_to_registered_channel_resolver()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var address = new ResolvedChannelAddress(key, ChannelAddressKind.User, "U123", "Alice Smith");
        var resolver = new TestAddressResolver(key, ChannelAddressKind.User)
        {
            Result = ChannelAddressResolutionResult.Resolved(address)
        };
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.User)],
            [resolver]);
        var tool = new LookupChannelUserTool(registry);

        var result = await ExecuteAsync(tool, "slack", "alice");

        Assert.Contains("Resolved user on channel 'slack'", result);
        Assert.Contains("channel_key: slack", result);
        Assert.Contains("stable_id: U123", result);
        Assert.Contains("display_name: Alice Smith", result);
        Assert.Contains("address_kind: user", result);
        Assert.Equal("alice", resolver.Request?.Query);
    }

    [Fact]
    public async Task Destination_lookup_formats_ambiguous_candidates()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var resolver = new TestAddressResolver(key, ChannelAddressKind.Destination)
        {
            Result = ChannelAddressResolutionResult.Ambiguous(
            [
                new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C1", "#general"),
                new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C2", "#general-private")
            ],
            "Multiple destinations matched.")
        };
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.Destination)],
            [resolver]);
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "slack", "general");

        Assert.Contains("Ambiguous destination lookup", result);
        Assert.Contains("channel_key: slack", result);
        Assert.Contains("stable_id: C1", result);
        Assert.Contains("stable_id: C2", result);
        Assert.Contains("address_kind: destination", result);
        Assert.Contains("Multiple destinations matched.", result);
    }

    [Fact]
    public async Task Lookup_rejects_disabled_channel_descriptor()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: false, ChannelAddressKind.User));
        var tool = new LookupChannelUserTool(registry);

        var result = await ExecuteAsync(tool, "slack", "alice");

        Assert.Contains("Channel 'slack' is disabled", result);
    }

    [Fact]
    public async Task Destination_lookup_reports_missing_discord_resolver()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelAddressKind.Destination));
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "discord", "general");

        Assert.Contains("No channel address resolver is registered for key 'discord'", result);
    }

    [Fact]
    public async Task Blank_query_on_destination_lookup_lists_deliverable_destinations()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var resolver = new TestAddressResolver(key, ChannelAddressKind.Destination)
        {
            ListResult = ChannelAddressResolutionResult.Listed(
            [
                new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C1", "#general"),
                new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C2", "#ops")
            ])
        };
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.Destination)],
            [resolver]);
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "slack", query: null);

        Assert.Contains("can deliver to 2 destination(s)", result);
        Assert.Contains("stable_id: C1", result);
        Assert.Contains("display_name: #ops", result);
    }

    [Fact]
    public async Task Blank_query_listing_with_no_destinations_explains_why()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);
        var resolver = new TestAddressResolver(key, ChannelAddressKind.Destination)
        {
            ListResult = ChannelAddressResolutionResult.Listed([])
        };
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Mattermost, isEnabled: true, ChannelAddressKind.Destination)],
            [resolver]);
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "mattermost", query: null);

        Assert.Contains("no destinations it can currently deliver to", result);
    }

    [Fact]
    public async Task Blank_query_listing_caps_output_and_reports_remainder()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var destinations = Enumerable.Range(1, 53)
            .Select(i => new ResolvedChannelAddress(key, ChannelAddressKind.Destination, $"C{i}", $"#room-{i}"))
            .ToArray();
        var resolver = new TestAddressResolver(key, ChannelAddressKind.Destination)
        {
            ListResult = ChannelAddressResolutionResult.Listed(destinations)
        };
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.Destination)],
            [resolver]);
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "slack", query: null);

        Assert.Contains("can deliver to 53 destination(s)", result);
        Assert.Contains("stable_id: C50", result);
        Assert.DoesNotContain("stable_id: C51;", result);
        Assert.Contains("and 3 more", result);
    }

    [Fact]
    public async Task Blank_query_listing_surfaces_unsupported_resolver()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);
        // No ListResult set: the resolver answers Unsupported, mirroring the
        // IChannelAddressResolver interface default for non-opted-in resolvers.
        var resolver = new TestAddressResolver(key, ChannelAddressKind.Destination);
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelAddressKind.Destination)],
            [resolver]);
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "discord", query: null);

        Assert.Contains("Error:", result);
        Assert.Contains("does not support destination listing", result);
    }

    [Fact]
    public async Task Blank_query_on_user_lookup_remains_an_error()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var resolver = new TestAddressResolver(key, ChannelAddressKind.User);
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.User)],
            [resolver]);
        var tool = new LookupChannelUserTool(registry);

        var result = await ExecuteAsync(tool, "slack", query: null);

        Assert.Contains("Error: 'query' parameter is required.", result);
    }

    [Fact]
    public void Query_is_optional_for_destination_lookup_but_required_for_user_lookup()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.User, ChannelAddressKind.Destination));

        var destinationRequired = ReadRequired(new LookupChannelDestinationTool(registry).ParameterSchema);
        var userRequired = ReadRequired(new LookupChannelUserTool(registry).ParameterSchema);

        Assert.DoesNotContain("query", destinationRequired);
        Assert.Contains("query", userRequired);
        Assert.Contains("channel_key", destinationRequired);
    }

    private static string[] ReadRequired(JsonElement schema)
    {
        return schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    private static Task<string> ExecuteAsync(ChannelLookupTool tool, string channelKey, string? query)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["channel_key"] = channelKey,
            ["_rationale"] = "test"
        };
        if (query is not null)
            arguments["query"] = query;

        return tool.ExecuteAsync(arguments, TestToolExecutionContext.CreateUnboundWithoutApproval(), TestContext.Current.CancellationToken);
    }

    private static string[] ReadChannelKeyEnum(JsonElement schema)
    {
        return schema
            .GetProperty("properties")
            .GetProperty("channel_key")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    private static ServiceCollection BuildServices(IReadOnlyDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        // The generic lookup tools are registered by the remote chat channel
        // builder when at least one channel is enabled.
        services.AddChannelIntegrations(configuration);
        return services;
    }

    private static bool IsRegistered<T>(IServiceCollection services)
    {
        return services.Any(descriptor => descriptor.ServiceType == typeof(T));
    }

    private static ChannelRegistry BuildRegistry(params ChannelDescriptor[] descriptors)
    {
        return BuildRegistry(descriptors, []);
    }

    private static ChannelRegistry BuildRegistry(
        IReadOnlyList<ChannelDescriptor> descriptors,
        IReadOnlyList<IChannelAddressResolver> resolvers)
    {
        var providers = descriptors.Select(descriptor => new StaticChannelDescriptorProvider(descriptor)).ToArray();
        return new ChannelRegistry(providers, [], resolvers);
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelType channelType,
        bool isEnabled,
        params ChannelAddressKind[] addressKinds)
    {
        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(channelType),
            channelType,
            channelType == ChannelType.Tui ? ChannelKind.LocalInteractiveClient : ChannelKind.RemoteChat,
            channelType.ToString(),
            isEnabled,
            ChannelCapabilities.SendMessages,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(addressKinds),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }

    private sealed class TestAddressResolver(
        ChannelDescriptorKey key,
        params ChannelAddressKind[] addressKinds) : IChannelAddressResolver
    {
        public ChannelDescriptorKey Key { get; } = key;

        public IReadOnlySet<ChannelAddressKind> AddressKinds { get; } = new HashSet<ChannelAddressKind>(addressKinds);

        public ChannelAddressResolutionRequest? Request { get; private set; }

        public ChannelAddressResolutionResult Result { get; init; } = ChannelAddressResolutionResult.NotFound();

        /// <summary>
        /// When null, listing answers the same loud Unsupported the
        /// IChannelAddressResolver interface default produces (an explicit
        /// implementation shadows the default, so it is replicated here).
        /// </summary>
        public ChannelAddressResolutionResult? ListResult { get; init; }

        public ValueTask<ChannelAddressResolutionResult> ResolveAsync(
            ChannelAddressResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(Result);
        }

        public ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
            CancellationToken cancellationToken = default)
        {
            return ListResult is not null
                ? ValueTask.FromResult(ListResult)
                : ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported(
                    $"Channel '{Key}' does not support destination listing."));
        }
    }
}
