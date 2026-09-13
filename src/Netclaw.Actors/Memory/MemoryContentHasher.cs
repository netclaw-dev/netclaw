// -----------------------------------------------------------------------
// <copyright file="MemoryContentHasher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Computes the content hash stored in <c>memory_embeddings.content_hash</c> (memory-core-
/// redesign D3). An embedding is only ever recomputed when this hash changes for the item, so
/// re-running backfill on an unchanged corpus is free. Normalization intentionally reuses
/// <see cref="CurationRulesEvaluator.NormalizeForContainment"/> (lowercase, whitespace-collapse)
/// rather than a second hand-rolled normalizer, so the two "does this content actually differ"
/// judgments in the memory subsystem — curation's destructive-update guard and the embedding
/// re-embed skip — can never quietly disagree about what counts as a change.
/// </summary>
public static class MemoryContentHasher
{
    /// <summary>
    /// SHA-256 hex digest (lowercase) of the normalized <c>"{title}\n{body}"</c>
    /// representation of a memory item.
    /// </summary>
    public static string ComputeHash(string title, string body)
    {
        var normalized = CurationRulesEvaluator.NormalizeForContainment(title)
            + "\n"
            + CurationRulesEvaluator.NormalizeForContainment(body);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
