// -----------------------------------------------------------------------
// <copyright file="ShellProgramPath.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The Bash rule that makes a program path name a file, not a spelling (R1).
/// </summary>
/// <remarks>
/// <para>
/// Bash runs a command word that contains a slash as a path, and it does not
/// search <c>PATH</c> for that word. So <c>./ilspycmd</c> in
/// <c>~/.dotnet/tools</c>, <c>~/.dotnet/tools/ilspycmd</c>, and
/// <c>/home/user/.dotnet/tools/ilspycmd</c> run one file. A grant uses the
/// absolute path of that file. A bare name such as <c>dotnet</c> does not
/// change, because the shell finds it through <c>PATH</c>.
/// </para>
/// <para>
/// The rule is lexical. It does not read the file system. The approval matcher
/// keeps an occurrence unresolved when a <c>..</c> segment follows a link, so a
/// lexical <c>..</c> here names the same file as the operating system.
/// </para>
/// <para>
/// Owner: the shell candidate builder applies the rule to each call. The
/// approval store applies the same rule to grants that older versions saved.
/// The data is call-local. The store keeps the absolute path, which is durable.
/// </para>
/// </remarks>
public static class ShellProgramPath
{
    private const string CurrentDirectoryPrefix = "./";

    private static readonly System.Buffers.SearchValues<char> ShellSyntaxCharacters =
        System.Buffers.SearchValues.Create("=$`\"'\\*?[]{}");

    /// <summary>Returns true when Bash runs <paramref name="programWord"/> as a path.</summary>
    public static bool IsPath(string programWord)
    {
        ArgumentNullException.ThrowIfNull(programWord);
        return programWord.Contains('/', StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the absolute path of a program word, or false when the word is not
    /// a path, starts with <c>~</c>, or is relative and has no absolute base.
    /// </summary>
    /// <param name="programWord">The program word after shell expansion.</param>
    /// <param name="workingDirectory">The effective working directory of the occurrence.</param>
    /// <param name="absolutePath">The lexical absolute path.</param>
    public static bool TryResolve(string programWord, string? workingDirectory, out string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(programWord);
        absolutePath = string.Empty;
        if (!IsPath(programWord) || programWord.StartsWith('~'))
            return false;

        if (programWord.StartsWith('/'))
        {
            absolutePath = NormalizeAbsolute(programWord);
            return true;
        }

        if (workingDirectory is null || !IsPosixAbsolute(workingDirectory))
            return false;

        absolutePath = NormalizeAbsolute(workingDirectory + "/" + programWord);
        return true;
    }

    /// <summary>
    /// Returns the lexical POSIX form of an absolute path. Empty and <c>.</c>
    /// segments go. A <c>..</c> segment removes the segment before it, and a
    /// <c>..</c> above the root stays at the root.
    /// </summary>
    public static string NormalizeAbsolute(string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        if (!absolutePath.StartsWith('/'))
            throw new ArgumentException("The path must be an absolute POSIX path.", nameof(absolutePath));

        var segments = new List<string>();
        foreach (var segment in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// Returns the path of <paramref name="absolutePath"/> below
    /// <paramref name="worktreeRoot"/> in the form <c>./a/b</c>, or null when the
    /// path is not below the root. A repository grant stores this form, so one
    /// grant covers the same file in every worktree of the repository.
    /// </summary>
    public static string? ToWorktreeRelative(string absolutePath, string worktreeRoot)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        ArgumentNullException.ThrowIfNull(worktreeRoot);
        if (!IsPosixAbsolute(absolutePath) || !IsPosixAbsolute(worktreeRoot))
            return null;

        var path = NormalizeAbsolute(absolutePath);
        var root = NormalizeAbsolute(worktreeRoot);
        var prefix = root == "/" ? root : root + "/";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal)
            ? CurrentDirectoryPrefix + path[prefix.Length..]
            : null;
    }

    /// <summary>
    /// Returns a relative program path in the form <c>./a/b</c>, or null when the
    /// path leaves its start directory (<c>../x</c>) or names no file.
    /// </summary>
    public static string? NormalizeRelative(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!IsPath(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('~'))
            return null;

        var segments = new List<string>();
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return null;

                segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return segments.Count == 0 || relativePath.EndsWith('/')
            ? null
            : CurrentDirectoryPrefix + string.Join('/', segments);
    }

    /// <summary>
    /// Returns true when an older grant with a relative program and no scope
    /// directory covers <paramref name="candidateProgram"/>.
    /// </summary>
    /// <remarks>
    /// SECURITY: such a grant has no base directory, so it never named one file.
    /// It matched its spelling from every directory. So it covers the files that
    /// that spelling can reach: the files whose path ends with the grant's named
    /// segments. <c>./tool</c> covers <c>/any/dir/tool</c>, not
    /// <c>/any/dir/mytool</c>. The grant covers no file that it did not cover
    /// before, and its old spelling still matches when the call has no known
    /// working directory.
    /// </remarks>
    public static bool MatchesLegacyRelative(string grantProgram, string candidateProgram)
    {
        ArgumentNullException.ThrowIfNull(grantProgram);
        ArgumentNullException.ThrowIfNull(candidateProgram);
        if (string.Equals(grantProgram, candidateProgram, StringComparison.Ordinal))
            return true;

        if (!IsLegacyRelative(grantProgram) || !IsPosixAbsolute(candidateProgram))
            return false;

        var namedSegments = string.Join(
            '/',
            grantProgram
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(static segment => segment is "." or ".."));
        if (namedSegments.Length == 0
            || namedSegments.Split('/').Any(static segment => segment is "." or ".."))
        {
            return false;
        }

        return NormalizeAbsolute(candidateProgram).EndsWith("/" + namedSegments, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true when <paramref name="programWord"/> is a relative path that an
    /// older grant saved without a base directory.
    /// </summary>
    public static bool IsLegacyRelative(string programWord)
    {
        ArgumentNullException.ThrowIfNull(programWord);
        return IsPath(programWord)
               && !programWord.StartsWith('/')
               && !programWord.StartsWith('~')
               && programWord.AsSpan().IndexOfAny(ShellSyntaxCharacters) < 0;
    }

    /// <summary>
    /// Rewrites the program path of a Bash grant that an older version saved as
    /// its spelling. A <c>~/x</c> or <c>/abs/x</c> program becomes its absolute
    /// path. A relative program joins the folder of a folder grant. A repository
    /// grant keeps the <c>./a/b</c> form, relative to the worktree root. A relative
    /// program with no scope directory stays as it is: see
    /// <see cref="MatchesLegacyRelative"/>.
    /// </summary>
    /// <param name="entry">The stored entry.</param>
    /// <param name="homeDirectory">The home directory that the shell launcher gives to <c>HOME</c>.</param>
    public static ApprovalEntry NormalizeGrant(ApprovalEntry entry, string? homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Shell != ApprovalShell.Bash || entry.Match is not { } match)
            return entry;

        var program = match == ApprovalMatchKind.TokenPrefix
            ? entry.VerbTokens![0]
            : FirstWord(entry.Verb);
        var resolved = ResolveStoredProgram(program, entry, homeDirectory);
        if (resolved is null || string.Equals(resolved, program, StringComparison.Ordinal))
            return entry;

        if (match == ApprovalMatchKind.LegacyExact)
        {
            return ApprovalEntry.CreateLegacyExact(
                ApprovalShell.Bash,
                resolved + entry.Verb[program.Length..],
                entry.Directory,
                entry.CreatedAt);
        }

        var tokens = entry.VerbTokens!.ToArray();
        tokens[0] = resolved;
        return ApprovalEntry.CreateTokenPrefix(
                ApprovalShell.Bash,
                tokens,
                entry.Directory,
                entry.CreatedAt,
                entry.AssignmentDigest)
            with
            {
                Repository = entry.Repository,
            };
    }

    private static string? ResolveStoredProgram(string program, ApprovalEntry entry, string? homeDirectory)
    {
        // An older phrase can start with an assignment or an expansion
        // ("MODE=$HOME/x bash"). Such a word is not a plain program path.
        if (!IsPath(program) || program.AsSpan().IndexOfAny(ShellSyntaxCharacters) >= 0)
            return null;

        if (program == "~" || program.StartsWith("~/", StringComparison.Ordinal))
        {
            return homeDirectory is not null && IsPosixAbsolute(homeDirectory)
                ? NormalizeAbsolute(homeDirectory + program[1..])
                : null;
        }

        if (program.StartsWith('~'))
            return null;

        if (program.StartsWith('/'))
            return NormalizeAbsolute(program);

        if (entry.Directory is { } directory)
            return TryResolve(program, directory, out var absolute) ? absolute : null;

        return entry.Repository is not null ? NormalizeRelative(program) : null;
    }

    private static string FirstWord(string verb)
    {
        var space = verb.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? verb : verb[..space];
    }

    private static bool IsPosixAbsolute(string path)
        => path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal);
}
