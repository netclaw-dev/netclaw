// -----------------------------------------------------------------------
// <copyright file="ApprovalGrantHygiene.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>Why <c>netclaw doctor</c> reports a stored grant.</summary>
public enum ApprovalHygieneIssue
{
    /// <summary>
    /// A folder grant has a command word after the verb slot that names an entry
    /// of its folder. The grant stays: it still covers the call in a subfolder.
    /// </summary>
    FileWord = 0,

    /// <summary>
    /// Another grant covers each call that this grant covers: the same words, or
    /// a verb grant whose words start this grant's words.
    /// </summary>
    Covered = 1,

    /// <summary>The folder of the grant does not exist. The doctor does not guess, so the grant stays.</summary>
    MissingFolder = 2,
}

/// <summary>One stored grant that <c>netclaw doctor</c> reports.</summary>
public sealed record ApprovalHygieneFinding(
    string Audience,
    string ToolName,
    ApprovalEntry Entry,
    ApprovalHygieneIssue Issue,
    string Detail)
{
    /// <summary>True when <c>netclaw doctor --fix</c> removes the grant: only a covered grant.</summary>
    public bool Removable => Issue == ApprovalHygieneIssue.Covered;
}

/// <summary>
/// The findings of one store and the store text without the removable grants.
/// </summary>
public sealed record ApprovalHygieneReport(
    IReadOnlyList<ApprovalHygieneFinding> Findings,
    string? OriginalText,
    string? UpdatedText);

/// <summary>
/// The rules that keep the grant store free of junk: no folder grant with a
/// file word, and no grant that another grant covers.
/// </summary>
/// <remarks>
/// SECURITY: these rules only refuse or remove grants, and they read only the
/// grant data. A grant covers another grant only when both have the same tool,
/// shell, and assignment digest, the words of the covering grant cover the
/// other words by the approval matcher rule
/// (<see cref="ToolApprovalEntryComparer.CoversCommandWords"/>), and the
/// covering grant applies "anywhere" or has the same scope. A folder never covers another folder,
/// and a repository never covers a folder: a link or a nested repository can
/// put a directory of the narrower scope outside the wider one. So a removal
/// never changes an allowed decision.
/// </remarks>
public static class ApprovalGrantHygiene
{
    /// <summary>Returns the command words of a grant.</summary>
    public static IReadOnlyList<string> Words(ApprovalEntry entry)
        => entry.Match == ApprovalMatchKind.TokenPrefix && entry.VerbTokens is { } tokens
            ? tokens
            : entry.Shell is null
                ? [entry.Verb]
                : entry.Verb.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Returns the file words of a shell grant in <paramref name="directory"/>
    /// (<see cref="ShellGrantFileWords"/>).
    /// </summary>
    public static IReadOnlyList<string> FileWords(ApprovalEntry entry, string? directory)
        => entry.Shell is null ? [] : ShellGrantFileWords.Find(Words(entry), directory);

    /// <summary>
    /// Returns true when <paramref name="wider"/> covers each call that
    /// <paramref name="narrower"/> covers, from the grant data alone.
    /// </summary>
    public static bool Covers(ApprovalEntry wider, ApprovalEntry narrower)
        => CoversPhrase(wider, narrower)
           && (wider is { Repository: null, Directory: null } || SameScope(wider, narrower));

    private static bool SameScope(ApprovalEntry left, ApprovalEntry right)
        => left.Repository is not null
            ? right.Repository is not null
              && ToolApprovalEntryComparer.Equals(
                  ToolApprovalEntryComparer.NormalizeDirectory(left.Repository),
                  ToolApprovalEntryComparer.NormalizeDirectory(right.Repository))
            : right.Repository is null
              && left.Directory is not null
              && right.Directory is not null
              && ToolApprovalEntryComparer.Equals(
                  ToolApprovalEntryComparer.NormalizeDirectory(left.Directory, left.Shell),
                  ToolApprovalEntryComparer.NormalizeDirectory(right.Directory, right.Shell));

    // The phrase of the wider grant covers the phrase of the narrower grant: the
    // same shell and assignment digest, and the words of the approval matcher
    // rule (ToolApprovalEntryComparer.CoversCommandWords). A non-shell grant
    // names a tool, so its phrase must be equal. A grant with a legacy relative
    // program covers files that its words do not name, so it is never compared.
    private static bool CoversPhrase(ApprovalEntry wider, ApprovalEntry narrower)
    {
        if (wider.Shell != narrower.Shell
            || wider.AssignmentDigest != narrower.AssignmentDigest
            || wider.HasLegacyProgramSpelling
            || narrower.HasLegacyProgramSpelling)
        {
            return false;
        }

        var widerWords = Words(wider);
        var narrowerWords = Words(narrower);
        return wider.Shell is { } shell
            ? ToolApprovalEntryComparer.CoversCommandWords(widerWords, narrowerWords, shell)
            : widerWords.Count == narrowerWords.Count
              && widerWords.Zip(narrowerWords).All(static pair => ToolApprovalEntryComparer.Equals(pair.First, pair.Second));
    }

    /// <summary>
    /// Returns the findings of one grant list. A grant that another grant covers
    /// is a finding. Of two grants that cover each other, the legacy phrase is the
    /// finding, or else the later grant, so the canonical grant stays.
    /// </summary>
    /// <remarks>
    /// Only a covered grant is removable. A folder grant with a file word of its
    /// own folder is reported and kept: the word is a command word in a subfolder,
    /// where the grant still covers the call. A grant without a folder does not
    /// record where its command ran, so a file word in it is never a finding.
    /// </remarks>
    public static IReadOnlyList<ApprovalHygieneFinding> Analyze(
        string audience,
        string toolName,
        IReadOnlyList<ApprovalEntry> entries)
    {
        var findings = new List<ApprovalHygieneFinding>();
        var removed = new HashSet<int>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is { Repository: null, Directory: { } folder } && !Directory.Exists(folder))
            {
                findings.Add(new(audience, toolName, entry, ApprovalHygieneIssue.MissingFolder, folder));
                continue;
            }

            if (FindCoveringGrant(entries, index, removed) is { } covering)
            {
                findings.Add(new(audience, toolName, entry, ApprovalHygieneIssue.Covered, covering.FormatScope()));
                removed.Add(index);
                continue;
            }

            if (entry is { Repository: null, Directory: { } own } && FileWords(entry, own) is { Count: > 0 } fileWords)
                findings.Add(new(audience, toolName, entry, ApprovalHygieneIssue.FileWord, string.Join(", ", fileWords)));
        }

        return findings;
    }

    private static ApprovalEntry? FindCoveringGrant(IReadOnlyList<ApprovalEntry> entries, int index, HashSet<int> removed)
    {
        var entry = entries[index];
        for (var other = 0; other < entries.Count; other++)
        {
            if (other == index || removed.Contains(other) || !Covers(entries[other], entry))
                continue;

            // Of two grants that cover each other, keep the canonical one.
            if (Covers(entry, entries[other]) && KeepsOver(entry, entries[other], index, other))
                continue;

            return entries[other];
        }

        return null;
    }

    // True when the first grant stays and the second goes: a token-prefix grant
    // stays over a legacy phrase, and else the earlier grant stays.
    private static bool KeepsOver(ApprovalEntry first, ApprovalEntry second, int firstIndex, int secondIndex)
    {
        var firstLegacy = first.Match == ApprovalMatchKind.LegacyExact;
        var secondLegacy = second.Match == ApprovalMatchKind.LegacyExact;
        return firstLegacy == secondLegacy ? firstIndex < secondIndex : secondLegacy;
    }
}
