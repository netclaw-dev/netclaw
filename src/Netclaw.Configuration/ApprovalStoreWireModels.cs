// -----------------------------------------------------------------------
// <copyright file="ApprovalStoreWireModels.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

internal sealed class ApprovalStoreWire
{
    public int Version { get; init; }

    public required Dictionary<string, Dictionary<string, List<ApprovalEntryWire>>> Audiences { get; init; }
}

[JsonDerivedType(typeof(TokenPrefixApprovalEntryWire))]
[JsonDerivedType(typeof(LegacyExactApprovalEntryWire))]
[JsonDerivedType(typeof(NonShellApprovalEntryWire))]
internal abstract class ApprovalEntryWire
{
    [JsonPropertyOrder(3)]
    public string? Directory { get; init; }

    [JsonPropertyOrder(4)]
    public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed class TokenPrefixApprovalEntryWire : ApprovalEntryWire
{
    [JsonPropertyOrder(0)]
    public required string? Shell { get; init; }

    [JsonPropertyOrder(1)]
    public required string? Match { get; init; }

    [JsonPropertyOrder(2)]
    public required string?[]? VerbTokens { get; init; }
}

internal sealed class LegacyExactApprovalEntryWire : ApprovalEntryWire
{
    [JsonPropertyOrder(0)]
    public required string? Shell { get; init; }

    [JsonPropertyOrder(1)]
    public required string? Match { get; init; }

    [JsonPropertyOrder(2)]
    public required string? Verb { get; init; }
}

internal sealed class NonShellApprovalEntryWire : ApprovalEntryWire
{
    [JsonPropertyOrder(0)]
    public required string? Verb { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApprovalStoreWire))]
[JsonSerializable(typeof(ApprovalEntryWire))]
[JsonSerializable(typeof(TokenPrefixApprovalEntryWire))]
[JsonSerializable(typeof(LegacyExactApprovalEntryWire))]
[JsonSerializable(typeof(NonShellApprovalEntryWire))]
internal sealed partial class ApprovalStoreJsonContext : JsonSerializerContext;
