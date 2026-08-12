// -----------------------------------------------------------------------
// <copyright file="ApprovalEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
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
    /// Absolute directory path the grant is scoped to, or <c>null</c> for
    /// the global wildcard. Trailing slashes are normalized away by the
    /// matcher so <c>/path/</c> and <c>/path</c> compare equal.
    /// </summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }

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
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(verbTokens);
        ApprovalEntryValidation.ValidateTokens(verbTokens);
        return new ApprovalEntry(string.Join(" ", verbTokens))
        {
            Shell = shell,
            Match = ApprovalMatchKind.TokenPrefix,
            VerbTokens = Array.AsReadOnly(verbTokens.ToArray()),
            Directory = directory,
            CreatedAt = createdAt,
        };
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
    /// a valid phrase remains unambiguous. Round-trips with
    /// <see cref="TryParseScope"/>.
    /// </summary>
    public string FormatScope()
    {
        var phrase = Shell is { } shell && Match is { } match
            ? $"{shell} {FormatMatch(match)} {JsonSerializer.Serialize(Verb)}"
            : $"NonShell exact {JsonSerializer.Serialize(Verb)}";
        return Directory is null ? $"{phrase} anywhere" : $"{phrase} in {Directory}";
    }

    /// <summary>
    /// Inverse of <see cref="FormatScope"/>. It also accepts the legacy
    /// untyped forms for compatibility. Returns false with a non-empty
    /// <paramref name="error"/> for any other shape so callers can surface the
    /// parse failure as a user error rather than a silent best-effort match.
    /// </summary>
    public static bool TryParseScope(string input, [NotNullWhen(true)] out ApprovalEntry? entry, out string error)
    {
        entry = null;
        error = string.Empty;

        const string AnywhereSuffix = " anywhere";
        const string InSeparator = " in ";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Approval scope must not be empty.";
            return false;
        }

        if (TryParseTypedScope(input, out entry, out error))
        {
            return true;
        }

        if (error.Length > 0)
        {
            return false;
        }

        if (input.EndsWith(AnywhereSuffix, StringComparison.Ordinal))
        {
            var verb = input[..^AnywhereSuffix.Length];
            if (verb.Length == 0)
            {
                error = "'<verb> anywhere' must include a verb.";
                return false;
            }
            entry = new ApprovalEntry(verb) { Directory = null };
            return true;
        }

        // First " in " separates verb from directory. Verb chains never
        // contain " in " as a literal token, so this split is unambiguous
        // for legitimate inputs.
        var inIndex = input.IndexOf(InSeparator, StringComparison.Ordinal);
        if (inIndex > 0)
        {
            var verb = input[..inIndex];
            var directory = input[(inIndex + InSeparator.Length)..];
            if (verb.Length == 0 || directory.Length == 0)
            {
                error = "'<verb> in <directory>' must include both verb and directory.";
                return false;
            }
            entry = new ApprovalEntry(verb) { Directory = directory };
            return true;
        }

        error = $"Could not parse approval scope '{input}'.";
        return false;
    }

    private static bool TryParseTypedScope(
        string input,
        [NotNullWhen(true)] out ApprovalEntry? entry,
        out string error)
    {
        entry = null;
        error = string.Empty;
        if (input.StartsWith("NonShell exact ", StringComparison.Ordinal))
        {
            var nonShellRemainder = input["NonShell exact ".Length..];
            if (!TryReadJsonString(
                    nonShellRemainder,
                    out var nonShellVerb,
                    out var nonShellTail) ||
                !TryReadScopeTail(nonShellTail, out var nonShellDirectory))
            {
                error = "The typed approval scope is invalid.";
                return false;
            }

            try
            {
                entry = CreateNonShell(nonShellVerb, nonShellDirectory);
                return true;
            }
            catch (ArgumentException)
            {
                error = "The typed approval scope is invalid.";
                return false;
            }
        }

        ApprovalShell shell;
        ApprovalMatchKind match;
        string remainder;
        if (input.StartsWith("Bash token-prefix ", StringComparison.Ordinal))
        {
            shell = ApprovalShell.Bash;
            match = ApprovalMatchKind.TokenPrefix;
            remainder = input["Bash token-prefix ".Length..];
        }
        else if (input.StartsWith("Bash legacy-exact ", StringComparison.Ordinal))
        {
            shell = ApprovalShell.Bash;
            match = ApprovalMatchKind.LegacyExact;
            remainder = input["Bash legacy-exact ".Length..];
        }
        else if (input.StartsWith("PowerShell token-prefix ", StringComparison.Ordinal))
        {
            shell = ApprovalShell.PowerShell;
            match = ApprovalMatchKind.TokenPrefix;
            remainder = input["PowerShell token-prefix ".Length..];
        }
        else if (input.StartsWith("PowerShell legacy-exact ", StringComparison.Ordinal))
        {
            shell = ApprovalShell.PowerShell;
            match = ApprovalMatchKind.LegacyExact;
            remainder = input["PowerShell legacy-exact ".Length..];
        }
        else
        {
            return false;
        }

        if (!TryReadJsonString(remainder, out var verb, out var tail) ||
            !TryReadScopeTail(tail, out var directory))
        {
            error = "The typed approval scope is invalid.";
            return false;
        }

        try
        {
            entry = match == ApprovalMatchKind.TokenPrefix
                ? CreateTokenPrefix(shell, verb.Split(' ', StringSplitOptions.RemoveEmptyEntries), directory)
                : CreateLegacyExact(shell, verb, directory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            error = "The typed approval scope is invalid.";
            return false;
        }
    }

    private static bool TryReadJsonString(
        string source,
        [NotNullWhen(true)] out string? value,
        out string tail)
    {
        value = null;
        tail = string.Empty;
        if (source.Length < 2 || source[0] != '"')
        {
            return false;
        }

        var escaped = false;
        for (var index = 1; index < source.Length; index++)
        {
            var current = source[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current != '"')
            {
                continue;
            }

            var encoded = source[..(index + 1)];
            try
            {
                value = JsonSerializer.Deserialize<string>(encoded);
            }
            catch (JsonException)
            {
                return false;
            }

            tail = source[(index + 1)..];
            return value is not null;
        }

        return false;
    }

    private static bool TryReadScopeTail(string tail, out string? directory)
    {
        const string InPrefix = " in ";
        directory = null;
        if (string.Equals(tail, " anywhere", StringComparison.Ordinal))
        {
            return true;
        }

        if (!tail.StartsWith(InPrefix, StringComparison.Ordinal) || tail.Length == InPrefix.Length)
        {
            return false;
        }

        directory = tail[InPrefix.Length..];
        return true;
    }

    private static string FormatMatch(ApprovalMatchKind match) => match switch
    {
        ApprovalMatchKind.TokenPrefix => "token-prefix",
        ApprovalMatchKind.LegacyExact => "legacy-exact",
        _ => throw new ArgumentOutOfRangeException(nameof(match), match, "The approval match kind is invalid."),
    };
}
