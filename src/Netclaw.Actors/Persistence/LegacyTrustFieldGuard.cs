// -----------------------------------------------------------------------
// <copyright file="LegacyTrustFieldGuard.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;

namespace Netclaw.Actors.Persistence;

/// <summary>
/// Detects legacy persisted job/reminder JSON documents that predate the
/// type-system-stiffening change (issue #994), which made <c>Audience</c> and
/// <c>Boundary</c> required.
///
/// A pre-#994 document either omits these keys entirely or carries an explicit
/// <c>null</c>. Such a document is rejected at load — it is NOT coerced to a
/// substituted audience. A job or reminder with no persisted trust context
/// cannot be run safely: the trust tier it should execute under is unknown, and
/// these features are typically disabled at the most-restrictive audience, so
/// substituting one would either escalate privilege or fabricate a nonsensical
/// state. The store fails the document loudly instead.
/// </summary>
internal static class LegacyTrustFieldGuard
{
    private const string AudienceKey = "audience";
    private const string BoundaryKey = "boundary";

    /// <summary>
    /// Returns the trust-field keys that are absent or explicitly null on the
    /// document, or an empty list when the document carries both (a current
    /// document) or cannot be parsed as a JSON object (left to the caller's
    /// normal parse-error handling).
    /// </summary>
    public static IReadOnlyList<string> MissingTrustFields(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
            return [];

        var missing = new List<string>(2);
        if (IsAbsentOrNull(root, AudienceKey))
            missing.Add(AudienceKey);
        if (IsAbsentOrNull(root, BoundaryKey))
            missing.Add(BoundaryKey);
        return missing;
    }

    // Web-serialized documents use camelCase keys; an older document could also
    // carry an explicit null where the field used to be nullable.
    private static bool IsAbsentOrNull(JsonObject root, string key)
        => !root.TryGetPropertyValue(key, out var value) || value is null;
}
