// -----------------------------------------------------------------------
// <copyright file="ApprovalGrantHygiene.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>Why <c>netclaw doctor</c> reports a stored grant.</summary>
public enum ApprovalHygieneIssue
{
    /// <summary>A folder grant has a command word after the verb slot that names an entry of its folder.</summary>
    FileWord = 0,

    /// <summary>Another grant with the same words covers each call that this grant covers.</summary>
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
    /// <summary>True when <c>netclaw doctor --fix</c> removes the grant.</summary>
    public bool Removable => Issue != ApprovalHygieneIssue.MissingFolder;
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
/// shell, words, and assignment digest, and the covering grant applies
/// "anywhere" or has the same scope. A folder never covers another folder,
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
        => SameIdentity(wider, narrower)
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

    // The same tool phrase: shell, words, and assignment digest. A grant with a
    // legacy relative program covers files that its words do not name, so it is
    // never compared.
    private static bool SameIdentity(ApprovalEntry left, ApprovalEntry right)
    {
        if (left.Shell != right.Shell
            || left.AssignmentDigest != right.AssignmentDigest
            || left.HasLegacyProgramSpelling
            || right.HasLegacyProgramSpelling)
        {
            return false;
        }

        var leftWords = Words(left);
        var rightWords = Words(right);
        return leftWords.Count == rightWords.Count
               && leftWords.Zip(rightWords).All(pair => left.Shell is { } shell
                   ? ToolApprovalEntryComparer.Equals(pair.First, pair.Second, shell)
                   : ToolApprovalEntryComparer.Equals(pair.First, pair.Second));
    }

    /// <summary>
    /// Returns the findings of one grant list. A grant that another grant covers
    /// is a finding, but only one of two grants that cover each other is.
    /// </summary>
    /// <remarks>
    /// Only a folder grant gets the file-word rule, in its own folder. A grant
    /// without a folder does not record where its command ran, so a file word
    /// in it is never a finding: the word can be a command word elsewhere.
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
            if (entry is { Repository: null, Directory: { } folder })
            {
                if (!Directory.Exists(folder))
                {
                    findings.Add(new(audience, toolName, entry, ApprovalHygieneIssue.MissingFolder, folder));
                    continue;
                }

                if (FileWords(entry, folder) is { Count: > 0 } fileWords)
                {
                    findings.Add(new(audience, toolName, entry, ApprovalHygieneIssue.FileWord, string.Join(", ", fileWords)));
                    removed.Add(index);
                    continue;
                }
            }

            for (var other = 0; other < entries.Count; other++)
            {
                if (other == index || removed.Contains(other) || !Covers(entries[other], entry))
                    continue;

                // Of two grants that cover each other, the later one is the finding.
                if (Covers(entry, entries[other]) && other > index)
                    continue;

                findings.Add(new(audience, toolName, entry, ApprovalHygieneIssue.Covered, entries[other].FormatScope()));
                removed.Add(index);
                break;
            }
        }

        return findings;
    }
}
