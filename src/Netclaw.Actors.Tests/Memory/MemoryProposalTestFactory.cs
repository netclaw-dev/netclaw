// -----------------------------------------------------------------------
// <copyright file="MemoryProposalTestFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Builds <see cref="MemoryProposal"/> instances from the wire-string
/// discriminators emitted by the distillation sidecar. Tests author proposals
/// in the model's wire vocabulary (<c>"upsert_document"</c>, <c>"durable_fact"</c>,
/// …) and this helper parses them into the typed enum fields, keeping the test
/// fixtures readable while exercising the same string→enum boundary the
/// production deserializer uses.
/// </summary>
internal static class MemoryProposalTestFactory
{
    public static MemoryProposal FromWire(
        string operation,
        string memoryClass,
        string subjectKind,
        string subjectValue,
        MemoryAnchor? anchor,
        string title,
        string content,
        IReadOnlyList<string>? aliases,
        IReadOnlyList<string>? facets,
        IReadOnlyList<string>? slots,
        IReadOnlyList<MemoryRelation>? relations,
        string recallMode,
        string sensitivity,
        double confidence,
        long? freshUntilMs,
        long? expiresAtMs,
        string? targetSurface,
        string? rationale)
    {
        if (!MemoryDomainEnumExtensions.TryFromWireValue(operation, out MemoryProposalOperation op))
            throw new ArgumentException($"Unknown operation wire value '{operation}'.", nameof(operation));
        if (!MemoryDomainEnumExtensions.TryFromWireValue(memoryClass, out MemoryClass mc))
            throw new ArgumentException($"Unknown memoryClass wire value '{memoryClass}'.", nameof(memoryClass));
        if (!MemoryDomainEnumExtensions.TryFromWireValue(recallMode, out MemoryRecallMode rm))
            throw new ArgumentException($"Unknown recallMode wire value '{recallMode}'.", nameof(recallMode));
        if (!MemoryDomainEnumExtensions.TryFromWireValue(sensitivity, out MemorySensitivity sens))
            throw new ArgumentException($"Unknown sensitivity wire value '{sensitivity}'.", nameof(sensitivity));

        return new MemoryProposal(
            op,
            mc,
            subjectKind,
            subjectValue,
            anchor,
            title,
            content,
            aliases,
            facets,
            slots,
            relations,
            rm,
            sens,
            confidence,
            freshUntilMs,
            expiresAtMs,
            targetSurface,
            rationale);
    }
}
