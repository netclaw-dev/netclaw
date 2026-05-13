// -----------------------------------------------------------------------
// <copyright file="HardDenyRule.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Security;

/// <summary>
/// Telemetry / log routing bucket for a deny decision. Wire format is
/// snake_case (<c>self_destructive</c>) so existing operator JSON files
/// and downstream log/metric labels keep working unchanged.
/// </summary>
/// <remarks>
/// The <see cref="DenyCategoryConverter"/> maps unrecognized wire values
/// to <see cref="Unknown"/> instead of throwing — operators can introduce
/// new category strings in their overrides without breaking the loader,
/// and forward-compat is preserved if shipped defaults later add new
/// categories.
/// </remarks>
[JsonConverter(typeof(DenyCategoryConverter))]
public enum DenyCategory
{
    Unknown,
    CustomDeny,
    SelfDestructive,
    SystemDestructive,
    PrivilegeEscalation,
}

public static class DenyCategoryExtensions
{
    /// <summary>
    /// Returns the snake_case wire-format name surfaced in JSON files,
    /// log lines, and metric labels.
    /// </summary>
    public static string ToWireName(this DenyCategory category) => category switch
    {
        DenyCategory.CustomDeny => "custom_deny",
        DenyCategory.SelfDestructive => "self_destructive",
        DenyCategory.SystemDestructive => "system_destructive",
        DenyCategory.PrivilegeEscalation => "privilege_escalation",
        _ => "unknown",
    };

    /// <summary>
    /// Parses a wire-format category name. Empty/null is treated as
    /// <see cref="DenyCategory.CustomDeny"/> (the operator-rule default);
    /// unrecognized non-empty strings return <see cref="DenyCategory.Unknown"/>.
    /// </summary>
    public static DenyCategory FromWireName(string? wire) => wire switch
    {
        null or "" => DenyCategory.CustomDeny,
        "custom_deny" => DenyCategory.CustomDeny,
        "self_destructive" => DenyCategory.SelfDestructive,
        "system_destructive" => DenyCategory.SystemDestructive,
        "privilege_escalation" => DenyCategory.PrivilegeEscalation,
        _ => DenyCategory.Unknown,
    };
}

internal sealed class DenyCategoryConverter : JsonConverter<DenyCategory>
{
    public override DenyCategory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DenyCategoryExtensions.FromWireName(reader.GetString());

    public override void Write(Utf8JsonWriter writer, DenyCategory value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToWireName());
}

/// <summary>
/// Operator-authored hard-deny rule loaded from
/// <c>~/.netclaw/config/hard-deny-overrides.json</c>. Each rule is a
/// structured predicate over a parsed shell clause (verb + args) plus an
/// explicit <c>rawText</c> escape for shape-shaped patterns that cannot be
/// expressed structurally (fork bombs, weird shell syntax).
/// </summary>
/// <remarks>
/// Override rules are strictly additive: they can introduce new deny rules
/// but cannot remove, disable, or weaken shipped defaults compiled into the
/// daemon binary. The DSL has no <c>disable</c> verb by design.
///
/// Exactly one match-shape field SHALL be set per rule:
/// <list type="bullet">
///   <item><see cref="Verb"/> — exact verb chain match (case-insensitive).</item>
///   <item><see cref="VerbPrefix"/> — first token starts with prefix (mkfs,
///         mkfs.ext4, etc).</item>
///   <item><see cref="RawText"/> — substring match against the rendered clause
///         (escape hatch for fork-bomb-shaped patterns).</item>
/// </list>
/// <see cref="ArgFlags"/> and <see cref="FirstPath"/> are optional refinements
/// applied on top of <see cref="Verb"/>.
/// </remarks>
public sealed class HardDenyRule
{
    /// <summary>
    /// Exact verb-chain match (case-insensitive). For <c>git push</c> the
    /// chain is <c>["git", "push"]</c>. The first N tokens of the parsed
    /// clause must equal these values exactly.
    /// </summary>
    [JsonPropertyName("verb")]
    public List<string>? Verb { get; set; }

    /// <summary>
    /// First-token prefix match (case-insensitive). Used for verb families
    /// like <c>mkfs.ext4</c>, <c>mkfs.xfs</c> where listing every variant
    /// is impractical.
    /// </summary>
    [JsonPropertyName("verbPrefix")]
    public string? VerbPrefix { get; set; }

    /// <summary>
    /// Optional refinement on a verb-matched rule: any of these flags must
    /// be present in the clause's argument tokens. Each entry is a literal
    /// flag string (e.g. <c>-rf</c>, <c>--recursive</c>).
    /// </summary>
    [JsonPropertyName("argFlags")]
    public List<string>? ArgFlags { get; set; }

    /// <summary>
    /// Optional refinement on a verb-matched rule: the first non-flag
    /// argument must be one of the listed paths. Trailing slashes are
    /// normalized away. Tilde and <c>$HOME</c> match the daemon-process
    /// user's home directory.
    /// </summary>
    [JsonPropertyName("firstPath")]
    public PathConstraint? FirstPath { get; set; }

    /// <summary>
    /// Substring match against the rendered clause. Escape hatch for
    /// shape-shaped patterns (fork bombs <c>:(){ :|:&amp; };:</c>) that
    /// cannot be expressed as verb+args. Marked
    /// <see cref="EscapeHatch"/>=true by convention.
    /// </summary>
    [JsonPropertyName("rawText")]
    public string? RawText { get; set; }

    /// <summary>
    /// User-visible explanation surfaced when the rule denies a call.
    /// </summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    /// <summary>
    /// Telemetry / log category. Defaults to
    /// <see cref="DenyCategory.CustomDeny"/> for operator overrides; shipped
    /// defaults populate this with <see cref="DenyCategory.SelfDestructive"/>
    /// or <see cref="DenyCategory.SystemDestructive"/>. Unrecognized wire
    /// values deserialize to <see cref="DenyCategory.Unknown"/> rather than
    /// throwing so forward-compat additions don't break the loader.
    /// </summary>
    [JsonPropertyName("category")]
    public DenyCategory Category { get; set; } = DenyCategory.CustomDeny;

    /// <summary>
    /// Documentation flag for <see cref="RawText"/> rules indicating the
    /// rule is using the rawText escape rather than structured matching.
    /// Exists purely for grep-ability — does not affect evaluation.
    /// </summary>
    [JsonPropertyName("escapeHatch")]
    public bool EscapeHatch { get; set; }

    /// <summary>
    /// Validates that the rule has exactly one match-shape field set and
    /// that refinements are only paired with <see cref="Verb"/>. Throws
    /// <see cref="InvalidDataException"/> with a clear message on any
    /// shape violation.
    /// </summary>
    public void Validate()
    {
        var shapeFields = 0;
        if (Verb is { Count: > 0 }) shapeFields++;
        if (!string.IsNullOrWhiteSpace(VerbPrefix)) shapeFields++;
        if (!string.IsNullOrWhiteSpace(RawText)) shapeFields++;

        if (shapeFields == 0)
            throw new InvalidDataException(
                $"Hard-deny rule (reason='{Reason}') has no match shape — set exactly one of 'verb', 'verbPrefix', or 'rawText'.");

        if (shapeFields > 1)
            throw new InvalidDataException(
                $"Hard-deny rule (reason='{Reason}') has multiple match shapes set — pick exactly one of 'verb', 'verbPrefix', or 'rawText'.");

        if ((ArgFlags is { Count: > 0 } || FirstPath is not null) && Verb is not { Count: > 0 })
            throw new InvalidDataException(
                $"Hard-deny rule (reason='{Reason}') uses 'argFlags' or 'firstPath' refinement without a 'verb' match. Refinements only apply to verb-matched rules.");

        if (string.IsNullOrWhiteSpace(Reason))
            throw new InvalidDataException(
                "Hard-deny rule has no 'reason' — every rule must include a user-visible explanation.");
    }
}

/// <summary>
/// Constraint on a single positional argument. Currently only
/// <see cref="OneOf"/> is supported. Extension point for future predicates
/// (regex, glob, etc.) without breaking the JSON schema.
/// </summary>
public sealed class PathConstraint
{
    /// <summary>
    /// The argument must be one of these literal paths (case-insensitive).
    /// Trailing slashes are normalized away. Tilde and <c>$HOME</c> /
    /// <c>${HOME}</c> match the daemon-process user's home directory.
    /// </summary>
    [JsonPropertyName("oneOf")]
    public List<string>? OneOf { get; set; }
}
