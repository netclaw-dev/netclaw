// -----------------------------------------------------------------------
// <copyright file="MemorySidecarContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;
using Netclaw.Actors.Memory;

namespace Netclaw.Actors.Sessions;

public enum MemoryProposalOperation
{
    Unknown,
    UpsertDocument,
    AppendRecord,
    Ignore
}

/// <summary>
/// A single memory-curation proposal produced by the distillation sidecar.
/// </summary>
/// <remarks>
/// This record is the direct deserialization target of LLM-emitted JSON
/// (see <see cref="SessionMemoryObserverActor"/>). The enum-typed fields carry
/// <see cref="JsonConverter"/> attributes so the on-wire JSON keeps its
/// snake-case discriminator strings (<c>"upsert_document"</c>, <c>"durable_fact"</c>,
/// …) — the enum is an in-memory representation only.
/// <para>
/// <see cref="SubjectKind"/> is deliberately left a <see cref="string"/>: the
/// distillation prompt instructs the model to emit subject identifiers such as
/// <c>"project"</c> or <c>"event"</c> that fall outside the three-member
/// <see cref="Netclaw.Actors.Memory.SubjectKind"/> enum, so retyping it would
/// silently drop wire data. The proposal gate parses it leniently with
/// <c>TryFromWireValue</c> where the identity classification matters.
/// </para>
/// </remarks>
public sealed record MemoryProposal(
    [property: JsonConverter(typeof(MemoryProposalOperationJsonConverter))]
    MemoryProposalOperation Operation,
    [property: JsonConverter(typeof(MemoryClassJsonConverter))]
    MemoryClass MemoryClass,
    string SubjectKind,
    string SubjectValue,
    MemoryAnchor? Anchor,
    string Title,
    string Content,
    IReadOnlyList<string>? Aliases,
    IReadOnlyList<string>? Facets,
    IReadOnlyList<string>? Slots,
    IReadOnlyList<MemoryRelation>? Relations,
    [property: JsonConverter(typeof(MemoryRecallModeJsonConverter))]
    MemoryRecallMode RecallMode,
    [property: JsonConverter(typeof(MemorySensitivityJsonConverter))]
    MemorySensitivity Sensitivity,
    double Confidence,
    long? FreshUntilMs,
    long? ExpiresAtMs,
    string? TargetSurface,
    string? Rationale);

public sealed record RecallPlanningRequest(
    string SessionId,
    string Mode,
    string UserText,
    IReadOnlyList<string> RecentUserTurns,
    IReadOnlyList<string> RecentAssistantTurns,
    IReadOnlyList<string> RecentEntities,
    int MaxQueryTerms,
    int MaxResults);

public sealed record RecallQueryPlan(
    string Mode,
    string Intent,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> MemoryClasses,
    int MaxResults,
    bool AllowExpiredEvidence);

public sealed record MemoryAnchor(
    string CanonicalName,
    string AnchorType);

public sealed record MemoryRelation(
    string RelationType,
    MemoryAnchor TargetAnchor);
