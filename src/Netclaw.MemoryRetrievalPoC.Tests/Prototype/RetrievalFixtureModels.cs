// -----------------------------------------------------------------------
// <copyright file="RetrievalFixtureModels.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json;

namespace Netclaw.MemoryRetrievalPoC.Tests.Prototype;

internal sealed record RetrievalFixture(
    IReadOnlyList<SeedDocument> SeedDocuments,
    IReadOnlyList<SeedEdge> SeedEdges,
    IReadOnlyList<RetrievalCase> Cases)
{
    public static RetrievalFixture Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(x => x.EndsWith("retrieval-fixtures.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<RetrievalFixture>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
    }
}

internal sealed record SeedDocument(
    string DocumentId,
    string AnchorId,
    string AnchorType,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    string Title,
    string MarkdownBody,
    string MemoryClass,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence);

internal sealed record SeedEdge(
    string EdgeId,
    string FromAnchorId,
    string ToAnchorId,
    string RelationType,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence);

internal sealed record RetrievalCase(
    string Id,
    string Prompt,
    string? ExpectedTopDocumentId = null,
    IReadOnlyList<string>? ExpectedContainsDocumentIds = null,
    IReadOnlyDictionary<string, string>? ExpectedBundle = null,
    IReadOnlyList<string>? ForbiddenDocumentIds = null,
    bool ExpectEmpty = false);

internal sealed record RetrievedDocument(
    string DocumentId,
    string AnchorId,
    string CanonicalName,
    string Title,
    string Body,
    string MemoryClass,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence);

internal sealed record RetrievedEdge(
    string FromAnchorId,
    string ToAnchorId,
    string RelationType,
    double Confidence);

internal sealed record RetrievalHit(
    string DocumentId,
    string Title,
    double Score,
    IReadOnlyList<string> Reasons);

internal sealed record RetrievalBundle(
    IReadOnlyDictionary<string, RetrievalHit> Slots);

internal sealed record RetrievalExplanation(
    string Prompt,
    IReadOnlyList<string> Facets,
    IReadOnlyList<ExplainedHit> RankedHits,
    IReadOnlyDictionary<string, string> BundleSlots,
    IReadOnlyDictionary<string, IReadOnlyList<string>> InferredNeighbors);

internal sealed record ExplainedHit(
    string DocumentId,
    string Title,
    double Score,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Facets,
    IReadOnlyList<string> Slots);
