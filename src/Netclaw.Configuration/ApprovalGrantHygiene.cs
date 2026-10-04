// -----------------------------------------------------------------------
// <copyright file="ApprovalGrantHygiene.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The filesystem facts that grant hygiene needs to compare two grant scopes.
/// Netclaw.Security owns the folder and repository rules of approval matching,
/// so it supplies the implementation.
/// </summary>
public interface IApprovalScopeFacts
{
    /// <summary>
    /// Returns true when the folder grant for <paramref name="wider"/> covers
    /// each directory that a folder grant for <paramref name="narrower"/> covers.
    /// The narrower folder must exist.
    /// </summary>
    bool FolderCoversFolder(string wider, string narrower);

    /// <summary>
    /// Returns true when the repository grant for
    /// <paramref name="repositoryCommonDirectory"/> covers the existing
    /// <paramref name="folder"/>.
    /// </summary>
    bool RepositoryCoversFolder(string repositoryCommonDirectory, string folder);
}

/// <summary>One grant to save, with the directory of the command that it came from.</summary>
/// <param name="Entry">The grant.</param>
/// <param name="CommandDirectory">
/// The directory of the command, when the grant has no folder of its own. The
/// store applies the file-word rule in it. Null when the caller does not know
/// it; a folder grant always uses its own folder.
/// </param>
public sealed record ApprovalAddition(ApprovalEntry Entry, string? CommandDirectory);

/// <summary>Why <c>netclaw doctor</c> reports a stored grant.</summary>
public enum ApprovalHygieneIssue
{
    /// <summary>A command word after the verb slot names an existing file or directory.</summary>
    FileWord = 0,

    /// <summary>Another grant with the same words covers the same or a wider scope.</summary>
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
/// The rules that keep the grant store free of junk: no file word, no grant
/// that another grant covers.
/// </summary>
/// <remarks>
/// SECURITY: these rules only remove or refuse grants. A grant is covered only
/// when the covering grant has the same tool, the same shell, the same words,
/// and the same assignment digest, and its scope holds each directory of the
/// covered scope. So a removal never changes an allowed decision.
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
    /// Returns the file words of a shell grant: in its own folder, or else in
    /// <paramref name="commandDirectory"/>.
    /// </summary>
    public static IReadOnlyList<string> FileWords(ApprovalEntry entry, string? commandDirectory)
        => entry.Shell is null
            ? []
            : ShellGrantFileWords.Find(Words(entry), entry.Directory ?? commandDirectory);

    /// <summary>
    /// Returns true when <paramref name="wider"/> covers each call that
    /// <paramref name="narrower"/> covers.
    /// </summary>
    public static bool Covers(ApprovalEntry wider, ApprovalEntry narrower, IApprovalScopeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!SameIdentity(wider, narrower))
            return false;

        if (wider.Repository is null && wider.Directory is null)
            return true;

        if (wider.Repository is { } repository)
        {
            return narrower.Repository is not null
                ? ToolApprovalEntryComparer.Equals(
                    ToolApprovalEntryComparer.NormalizeDirectory(repository),
                    ToolApprovalEntryComparer.NormalizeDirectory(narrower.Repository))
                : narrower.Directory is { } folder && facts.RepositoryCoversFolder(repository, folder);
        }

        return narrower.Repository is null
               && narrower.Directory is { } narrowerFolder
               && facts.FolderCoversFolder(wider.Directory!, narrowerFolder);
    }

    // A device such as /dev/console exists as a file, but it has no length.
    private static bool IsFileWithContent(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0;
    }

    // The main worktree of a repository grant: the folder that holds its ".git" directory.
    private static string? WorktreeRoot(string? repositoryCommonDirectory)
        => repositoryCommonDirectory is not null
           && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(repositoryCommonDirectory)), ".git", StringComparison.Ordinal)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repositoryCommonDirectory))
            : null;

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
    /// is a finding, but only one of two equal grants is.
    /// </summary>
    /// <remarks>
    /// A folder grant gets the file-word rule in its own folder. A grant without
    /// a folder does not record where its command ran. Its evidence is the
    /// worktree root of a repository grant, and else each folder that the list
    /// names. There a word counts only when it names a file with content, such
    /// as <c>Phobos.slnx</c>. A directory, an empty file, or a device with the
    /// name of a subcommand (<c>search</c>, <c>build</c>, <c>/dev/console</c>) is
    /// not evidence.
    /// </remarks>
    public static IReadOnlyList<ApprovalHygieneFinding> Analyze(
        string audience,
        string toolName,
        IReadOnlyList<ApprovalEntry> entries,
        IApprovalScopeFacts facts)
    {
        var findings = new List<ApprovalHygieneFinding>();
        var folders = entries
            .Where(static entry => entry.Repository is null && entry.Directory is not null)
            .Select(static entry => entry.Directory!)
            .Concat(entries.Select(static entry => WorktreeRoot(entry.Repository)).OfType<string>())
            .Distinct(ToolApprovalEntryComparer.Comparer)
            .ToArray();
        var removed = new HashSet<int>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Repository is null
                && entry.Directory is { } folder
                && !Directory.Exists(folder))
            {
                findings.Add(new(audience, toolName, entry, ApprovalHygieneIssue.MissingFolder, folder));
                continue;
            }

            var evidence = entry.Directory is { } own
                ? [own]
                : WorktreeRoot(entry.Repository) is { } root ? [root] : folders;
            if (evidence.Select(directory => (Directory: directory, Words: entry.Directory is null
                        ? FileWords(entry, directory).Where(word => IsFileWithContent(Path.Join(directory, word))).ToArray()
                        : FileWords(entry, directory)))
                    .FirstOrDefault(static found => found.Words.Count > 0) is { Words.Count: > 0 } fileWords)
            {
                findings.Add(new(
                    audience,
                    toolName,
                    entry,
                    ApprovalHygieneIssue.FileWord,
                    $"{string.Join(", ", fileWords.Words)} in {fileWords.Directory}"));
                removed.Add(index);
                continue;
            }

            for (var other = 0; other < entries.Count; other++)
            {
                if (other == index || removed.Contains(other))
                    continue;

                // Of two grants that cover each other, the later one is the finding.
                var mutual = Covers(entry, entries[other], facts);
                if (Covers(entries[other], entry, facts) && (!mutual || other < index))
                {
                    findings.Add(new(
                        audience,
                        toolName,
                        entry,
                        ApprovalHygieneIssue.Covered,
                        entries[other].FormatScope()));
                    removed.Add(index);
                    break;
                }
            }
        }

        return findings;
    }
}
