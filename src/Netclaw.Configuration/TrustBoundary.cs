// -----------------------------------------------------------------------
// <copyright file="TrustBoundary.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Strongly-typed trust boundary — the security partition label that scopes
/// trusted delivery, persisted job/reminder records, and memory queries.
/// Wraps the boundary string so a partition label cannot be confused with any
/// other string at a call boundary. The canonical well-known boundaries are
/// exposed as named factories instead of magic string literals.
/// </summary>
public readonly record struct TrustBoundary
{
    /// <summary>
    /// Constructs a trust boundary from its canonical string value. The value
    /// is trimmed; an empty or whitespace value is rejected.
    /// </summary>
    public TrustBoundary(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Trust boundary must be a non-empty value.", nameof(value));

        Value = value.Trim();
    }

    /// <summary>The canonical boundary string (e.g. <c>boundary:public</c>).</summary>
    public string Value { get; }

    // Canonical boundary string values. Exposed as compile-time constants for
    // the few contexts that require one — default parameter values and
    // persistence-layer DTOs that map directly to a storage column. Prefer the
    // named factory properties below for in-memory code.
    public const string PublicValue = "boundary:public";
    public const string TeamValue = "boundary:team";
    public const string PersonalValue = "boundary:personal";
    public const string TrustedInstanceValue = "boundary:trusted-instance";
    public const string LegacyRestrictedValue = "boundary:legacy-restricted";

    /// <summary>The broadest, least-trusted partition.</summary>
    public static TrustBoundary Public { get; } = new(PublicValue);

    /// <summary>The team partition.</summary>
    public static TrustBoundary Team { get; } = new(TeamValue);

    /// <summary>The personal partition — the operator's own trust scope.</summary>
    public static TrustBoundary Personal { get; } = new(PersonalValue);

    /// <summary>
    /// The trusted-instance partition (a single Slack workspace, a local
    /// daemon). Also the canonical form of the legacy Slack-workspace and
    /// local-daemon boundary aliases.
    /// </summary>
    public static TrustBoundary TrustedInstance { get; } = new(TrustedInstanceValue);

    /// <summary>
    /// The most-restrictive partition assigned to records that predate
    /// explicit boundary persistence.
    /// </summary>
    public static TrustBoundary LegacyRestricted { get; } = new(LegacyRestrictedValue);

    /// <summary>
    /// Explicit conversion from the raw boundary string. Routes through the
    /// validating constructor — there is deliberately no implicit conversion.
    /// </summary>
    public static explicit operator TrustBoundary(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Serializes <see cref="TrustBoundary"/> as its bare primitive string so the
/// on-disk JSON document carries the boundary value verbatim — the value
/// object is an in-memory gate, not a wire-format change.
/// </summary>
public sealed class TrustBoundaryJsonConverter : JsonConverter<TrustBoundary>
{
    public override TrustBoundary Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Trust boundary value cannot be null."));

    public override void Write(
        Utf8JsonWriter writer, TrustBoundary value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

