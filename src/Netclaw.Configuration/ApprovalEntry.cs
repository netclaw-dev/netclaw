// -----------------------------------------------------------------------
// <copyright file="ApprovalEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// One persisted tool-approval grant paired with a directory scope.
/// <see cref="Directory"/> is null for the global wildcard
/// ("approve this verb in any directory"); otherwise it is an absolute path
/// and the entry only matches invocations whose cwd is under that path.
///
/// Version 3 uses typed shell phrases and compatible exact non-shell entries.
/// See <see cref="ToolApprovalStore.Load"/> for conversion behavior.
/// </summary>
/// <remarks>
/// Record-synthesized <c>Equals</c>/<c>GetHashCode</c> use default string
/// equality (case-sensitive Ordinal) which diverges from the canonical
/// approval comparison on Windows (OrdinalIgnoreCase) and does not normalize
/// trailing path separators. Use
/// <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>
/// when comparing entries for approval-store semantics; do not rely on the
/// record's built-in equality (e.g. <c>HashSet&lt;ApprovalEntry&gt;</c>,
/// <c>Enumerable.Distinct()</c>) for that purpose.
/// </remarks>
/// <param name="Verb">
/// The verb chain (e.g. <c>git remote</c>, <c>freshdesk</c>). For
/// <c>shell_execute</c> this is the prefix of non-flag tokens extracted
/// from a command; for other tools it is the tool name.
/// </param>
public sealed record ApprovalEntry([property: JsonPropertyName("verb")] string Verb)
{
    /// <summary>
    /// Creates an exact non-shell approval entry.
    /// </summary>
    public static ApprovalEntry CreateNonShell(
        string verb,
        string? directory = null,
        DateTimeOffset? createdAt = null)
    {
        ApprovalEntryValidation.ValidateVerb(verb);
        return new ApprovalEntry(verb)
        {
            Directory = directory,
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// The canonical shell for a typed shell phrase, or <c>null</c> for a
    /// non-shell approval.
    /// </summary>
    [JsonIgnore]
    public ApprovalShell? Shell { get; init; }

    /// <summary>
    /// The typed shell match rule, or <c>null</c> for a non-shell approval.
    /// </summary>
    [JsonIgnore]
    public ApprovalMatchKind? Match { get; init; }

    /// <summary>
    /// The immutable token sequence for a token-prefix shell phrase.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? VerbTokens { get; private init; }

    /// <summary>
    /// The exact shell-assignment constraint, or <c>null</c> when the grant
    /// covers a command with no bounded assignment facts.
    /// </summary>
    [JsonIgnore]
    public ApprovalAssignmentDigest? AssignmentDigest { get; init; }

    /// <summary>
    /// Absolute directory path the grant is scoped to, or <c>null</c> for
    /// the global wildcard. Trailing slashes are normalized away by the
    /// matcher so <c>/path/</c> and <c>/path</c> compare equal.
    /// </summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }

    /// <summary>
    /// The canonical Git common directory for an explicit repository grant.
    /// Folder and global grants leave this value null.
    /// </summary>
    [JsonPropertyName("repository")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repository { get; init; }

    /// <summary>
    /// When this grant was first persisted, or <c>null</c> for entries
    /// written before approval timestamps were tracked. Stamped by
    /// <see cref="ToolApprovalStore.AddApproval"/> at write time. This is an
    /// additive, optional field — its presence does not change the on-disk
    /// schema version. It is provenance only: it does NOT participate in
    /// approval matching or in
    /// <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>,
    /// so re-granting an existing approval keeps the original timestamp.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Creates a typed token-prefix shell entry.
    /// </summary>
    public static ApprovalEntry CreateTokenPrefix(
        ApprovalShell shell,
        IReadOnlyList<string> verbTokens,
        string? directory = null,
        DateTimeOffset? createdAt = null,
        ApprovalAssignmentDigest? assignmentDigest = null)
    {
        ArgumentNullException.ThrowIfNull(verbTokens);
        ApprovalEntryValidation.ValidateTokens(verbTokens);
        return new ApprovalEntry(ShellCommandWordText.FormatPhrase(shell, verbTokens))
        {
            Shell = shell,
            Match = ApprovalMatchKind.TokenPrefix,
            VerbTokens = Array.AsReadOnly(verbTokens.ToArray()),
            AssignmentDigest = assignmentDigest,
            Directory = directory,
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// Creates a typed shell phrase for registered worktrees of one repository.
    /// </summary>
    public static ApprovalEntry CreateRepositoryTokenPrefix(
        ApprovalShell shell,
        IReadOnlyList<string> verbTokens,
        string repository,
        DateTimeOffset? createdAt = null,
        ApprovalAssignmentDigest? assignmentDigest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var entry = CreateTokenPrefix(
            shell,
            verbTokens,
            createdAt: createdAt,
            assignmentDigest: assignmentDigest);
        return entry with { Repository = repository };
    }

    /// <summary>
    /// Creates a typed legacy-exact shell entry.
    /// </summary>
    public static ApprovalEntry CreateLegacyExact(
        ApprovalShell shell,
        string verb,
        string? directory = null,
        DateTimeOffset? createdAt = null)
    {
        ApprovalEntryValidation.ValidateVerb(verb);
        return new ApprovalEntry(verb)
        {
            Shell = shell,
            Match = ApprovalMatchKind.LegacyExact,
            Directory = directory,
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// The user-visible scope label emitted by <c>netclaw approvals list</c>
    /// and shown in the TUI. The phrase is JSON-quoted so separator text inside
    /// a valid phrase remains unambiguous. It is display text only: no code
    /// parses it back into an entry.
    /// </summary>
    public string FormatScope()
    {
        var phrase = Shell is { } shell && Match is { } match
            ? $"{shell} {FormatMatch(match)} {JsonSerializer.Serialize(Verb)}"
            : $"NonShell exact {JsonSerializer.Serialize(Verb)}";
        if (AssignmentDigest is { } assignmentDigest)
            phrase += $" with assignment {assignmentDigest.Value}";
        if (Repository is not null)
            phrase = $"{phrase} in repository {Repository}";
        else
            phrase = Directory is null ? $"{phrase} anywhere" : $"{phrase} in {Directory}";

        return HasLegacyProgramSpelling ? $"{phrase} (legacy program spelling)" : phrase;
    }

    /// <summary>
    /// True when an older version saved this Bash grant with a relative program
    /// path and no directory to resolve it against. The grant covers each file
    /// that the spelling can reach (see <see cref="ShellProgramPath.MatchesLegacyRelative"/>).
    /// Grant it again to cover one file.
    /// </summary>
    [JsonIgnore]
    public bool HasLegacyProgramSpelling
    {
        get
        {
            if (Shell != ApprovalShell.Bash || Directory is not null || Match is not { } match)
                return false;

            var program = match == ApprovalMatchKind.TokenPrefix
                ? VerbTokens![0]
                : Verb.Split(' ', 2)[0];
            return ShellProgramPath.IsLegacyRelative(program)
                   && (Repository is null || ShellProgramPath.NormalizeRelative(program) != program);
        }
    }

    private static string FormatMatch(ApprovalMatchKind match) => match switch
    {
        ApprovalMatchKind.TokenPrefix => "token-prefix",
        ApprovalMatchKind.LegacyExact => "legacy-exact",
        _ => throw new ArgumentOutOfRangeException(nameof(match), match, "The approval match kind is invalid."),
    };
}

/// <summary>
/// Identifies the exact bounded shell assignments that qualify one approval.
/// </summary>
public readonly record struct ApprovalAssignmentDigest
{
    internal const int CanonicalLength = 71;
    private const string Prefix = "sha256:";

    public ApprovalAssignmentDigest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsCanonical(value))
        {
            throw new ArgumentException(
                "The assignment digest must use canonical SHA-256 text.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the canonical digest text.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;

    internal static bool IsCanonical(string? value)
    {
        if (value is null || value.Length != CanonicalLength ||
            !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(Prefix.Length))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}
