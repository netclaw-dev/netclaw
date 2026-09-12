// -----------------------------------------------------------------------
// <copyright file="ToolFrictionEvidenceModels.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Security.Tests;

internal sealed record ToolFrictionFixtureCatalog
{
    public required int SchemaVersion { get; init; }

    public required ToolFrictionSanitization Sanitization { get; init; }

    public required List<ToolFrictionCase> Cases { get; init; }
}

internal sealed record ToolFrictionSanitization
{
    public required string SourceBoundary { get; init; }

    public required List<string> ProhibitedRawIdentifierClasses { get; init; }
}

internal sealed record ToolFrictionCase
{
    public required string Id { get; init; }

    public required string Scenario { get; init; }

    public required string ObservedFriction { get; init; }

    public required List<string> ExpectedToolSequence { get; init; }

    public required string ExpectedOutcome { get; init; }

    public required bool ExpectedApprovalRequired { get; init; }

    public required bool FallbackApprovalRequired { get; init; }

    public required string ExpectedContextEffect { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ToolFrictionFixtureCatalog))]
internal sealed partial class ToolFrictionEvidenceJsonContext : JsonSerializerContext;
