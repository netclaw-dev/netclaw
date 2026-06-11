// -----------------------------------------------------------------------
// <copyright file="ChannelLookupTools.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Channels;
using Netclaw.Tools;

namespace Netclaw.Daemon.Configuration;

// Registered once per host by the remote chat channel builder
// (RemoteChatChannelRegistrationExtensions.AddSharedChannelTools) whenever at
// least one remote chat channel is enabled.
internal sealed class LookupChannelUserTool(IChannelRegistry registry) : ChannelLookupTool(registry)
{
    public override string Name => "lookup_channel_user";

    public override string Description => "Look up a user on an enabled chat channel. Returns stable user IDs for channel-specific workflows.";

    protected override ChannelAddressKind AddressKind => ChannelAddressKind.User;

    protected override string LookupLabel => "user";

    protected override string QueryDescription => "User ID, username, display name, real name, or email address to resolve on the selected channel.";
}

internal sealed class LookupChannelDestinationTool(IChannelRegistry registry) : ChannelLookupTool(registry)
{
    public override string Name => "lookup_channel_destination";

    public override string Description => "Look up a destination on an enabled chat channel. Returns stable channel or destination IDs for channel-specific workflows. Omit 'query' to list every destination the channel can currently deliver to.";

    protected override ChannelAddressKind AddressKind => ChannelAddressKind.Destination;

    protected override string LookupLabel => "destination";

    protected override string QueryDescription => "Destination ID or name to resolve on the selected channel. Omit or leave blank to list all destinations the bot can deliver to.";

    // Blank-query listing is destination-only: the deliverable destination
    // set is bounded (bot memberships, guild channels, or a configured
    // allowlist), while user directories are unbounded and only make sense
    // as server-side searches — lookup_channel_user keeps requiring a query.
    protected override bool SupportsBlankQueryListing => true;
}

internal abstract class ChannelLookupTool : IChannelTool
{
    private readonly IChannelRegistry _registry;
    private AITool? _aiTool;
    private JsonElement? _parameterSchema;
    private LlmFacingToolName? _llmFacingName;

    protected ChannelLookupTool(IChannelRegistry registry)
    {
        _registry = registry;
    }

    public abstract string Name { get; }

    public LlmFacingToolName LlmFacingName => _llmFacingName ??= LlmFacingToolName.FromCanonical(Name);

    public abstract string Description { get; }

    public string GrantCategory => "builtin";

    public JsonElement ParameterSchema => _parameterSchema ??= BuildParameterSchema();

    protected abstract ChannelAddressKind AddressKind { get; }

    protected abstract string LookupLabel { get; }

    protected abstract string QueryDescription { get; }

    /// <summary>
    /// When true, a blank or missing 'query' triggers a listing of every
    /// deliverable destination instead of an error, and 'query' is omitted
    /// from the schema's required array.
    /// </summary>
    protected virtual bool SupportsBlankQueryListing => false;

    public AITool ToAITool()
    {
        return _aiTool ??= AIFunctionFactory.CreateDeclaration(Name, Description, ParameterSchema);
    }

    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        var channelKeyValue = ToolArgumentHelper.GetString(arguments, "channel_key");
        if (string.IsNullOrWhiteSpace(channelKeyValue))
            return "Error: 'channel_key' parameter is required.";

        var query = ToolArgumentHelper.GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query) && !SupportsBlankQueryListing)
            return "Error: 'query' parameter is required.";

        var key = ChannelDescriptorKey.Create(channelKeyValue.Trim());
        var enabledKeys = GetEnabledChannelKeys();

        ChannelDescriptor descriptor;
        try
        {
            descriptor = _registry.GetChannel(key);
        }
        catch (InvalidOperationException ex)
        {
            if (enabledKeys.Count == 0)
                return $"Error: No enabled channels support {LookupLabel} lookup.";

            return $"Error: {ex.Message} Supported channel_key values: {string.Join(", ", enabledKeys)}.";
        }

        if (!descriptor.IsEnabled)
            return $"Error: Channel '{key}' is disabled. Supported channel_key values: {string.Join(", ", enabledKeys)}.";

        if (!descriptor.AddressKinds.Contains(AddressKind))
            return $"Error: Channel '{key}' does not support {LookupLabel} lookup. Supported channel_key values: {string.Join(", ", enabledKeys)}.";

        ChannelAddressResolutionResult result;
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                result = await _registry.ListDestinationsAsync(key, ct);
                return FormatListing(key, result);
            }

            result = await _registry.ResolveAddressAsync(
                new ChannelAddressResolutionRequest(key, AddressKind, query.Trim()),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }

        return FormatResult(key, query.Trim(), result);
    }

    private JsonElement BuildParameterSchema()
    {
        var channelKeys = GetEnabledChannelKeys();
        var channelEnum = JsonSerializer.Serialize(channelKeys);
        var schemaJson = $$"""
        {
            "type": "object",
            "properties": {
                "channel_key": {
                    "type": "string",
                    "description": "Enabled channel key to search.",
                    "enum": {{channelEnum}}
                },
                "query": {
                    "type": "string",
                    "description": {{JsonSerializer.Serialize(QueryDescription)}}
                },
                "_rationale": {
                    "type": "string",
                    "description": "State your intent for this tool call in one sentence - what are you trying to accomplish and why?"
                },
                "_timeout_seconds": {
                    "type": "integer",
                    "description": "Requested timeout in seconds. Only set when the default is insufficient."
                },
                "_background": {
                    "type": "boolean",
                    "description": "Set to true to run this tool in the background and receive results later."
                }
            },
            "required": {{(SupportsBlankQueryListing
                ? """["channel_key", "_rationale"]"""
                : """["channel_key", "query", "_rationale"]""")}}
        }
        """;

        return JsonDocument.Parse(schemaJson).RootElement.Clone();
    }

    private IReadOnlyList<string> GetEnabledChannelKeys()
    {
        return _registry.ListChannels()
            .Where(descriptor => descriptor.IsEnabled && descriptor.AddressKinds.Contains(AddressKind))
            .Select(descriptor => descriptor.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private string FormatResult(
        ChannelDescriptorKey key,
        string query,
        ChannelAddressResolutionResult result)
    {
        return result.Status switch
        {
            ChannelAddressResolutionStatus.Resolved => FormatResolved(key, result.RequireSingle()),
            ChannelAddressResolutionStatus.Ambiguous => FormatAmbiguous(key, query, result),
            ChannelAddressResolutionStatus.NotFound => $"No {LookupLabel} found on channel '{key}' for query '{query}'.{FormatErrorSuffix(result.Error)}",
            ChannelAddressResolutionStatus.Unsupported => $"Error: {result.Error ?? $"Channel '{key}' does not support {LookupLabel} lookup."}",
            _ => $"Error: Unsupported address resolution status '{result.Status}'."
        };
    }

    private string FormatResolved(ChannelDescriptorKey key, ResolvedChannelAddress address)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Resolved {LookupLabel} on channel '{key}':");
        builder.AppendLine($"channel_key: {address.ChannelKey}");
        builder.AppendLine($"stable_id: {address.StableId}");
        builder.AppendLine($"display_name: {address.DisplayName}");
        builder.AppendLine($"address_kind: {ChannelAddressKindWire.ToWireValue(address.AddressKind)}");
        return builder.ToString().TrimEnd();
    }

    private const int MaxListedDestinations = 50;

    private string FormatListing(ChannelDescriptorKey key, ChannelAddressResolutionResult result)
    {
        if (result.Status == ChannelAddressResolutionStatus.Unsupported)
            return $"Error: {result.Error ?? $"Channel '{key}' does not support destination listing."}";

        if (result.Status != ChannelAddressResolutionStatus.Listed)
            return $"Error: Unexpected listing status '{result.Status}' from channel '{key}'.";

        if (result.Candidates.Count == 0)
            return $"Channel '{key}' has no destinations it can currently deliver to. Channels may need to invite the bot, or the operator may need to extend the channel allowlist.";

        var builder = new StringBuilder();
        builder.AppendLine($"Channel '{key}' can deliver to {result.Candidates.Count} destination(s):");
        foreach (var candidate in result.Candidates.Take(MaxListedDestinations))
            builder.AppendLine($"- channel_key: {candidate.ChannelKey}; stable_id: {candidate.StableId}; display_name: {candidate.DisplayName}; address_kind: {ChannelAddressKindWire.ToWireValue(candidate.AddressKind)}");

        if (result.Candidates.Count > MaxListedDestinations)
            builder.AppendLine($"…and {result.Candidates.Count - MaxListedDestinations} more. Use a 'query' to narrow the search.");

        return builder.ToString().TrimEnd();
    }

    private string FormatAmbiguous(
        ChannelDescriptorKey key,
        string query,
        ChannelAddressResolutionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Ambiguous {LookupLabel} lookup on channel '{key}' for query '{query}'.{FormatErrorSuffix(result.Error)}");
        builder.AppendLine("Candidates:");
        foreach (var candidate in result.Candidates)
            builder.AppendLine($"- channel_key: {candidate.ChannelKey}; stable_id: {candidate.StableId}; display_name: {candidate.DisplayName}; address_kind: {ChannelAddressKindWire.ToWireValue(candidate.AddressKind)}");

        return builder.ToString().TrimEnd();
    }

    private static string FormatErrorSuffix(string? error)
    {
        return string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error}";
    }
}
