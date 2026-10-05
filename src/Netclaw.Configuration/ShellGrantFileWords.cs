// -----------------------------------------------------------------------
// <copyright file="ShellGrantFileWords.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The one rule that keeps a file name out of the command words of a shell
/// grant. A command word after the verb slot that names an existing file or
/// directory in the command's directory is an operand, not a command word.
/// </summary>
/// <remarks>
/// <para>
/// The approval matcher applies the rule to the words of each call, the store
/// refuses a new grant that breaks it, and <c>netclaw doctor</c> reports a stored
/// grant that breaks it. All three use this type, so they cannot drift.
/// </para>
/// <para>
/// SECURITY: the program word and the verb slot never count. The verb slot can
/// name what runs: a subcommand (<c>git push</c>) or a script
/// (<c>bash deploy.sh</c>). A link never counts, because its target can be a
/// protected path. The rule reads the disk once for each word.
/// </para>
/// </remarks>
public static class ShellGrantFileWords
{
    /// <summary>The index of the first word after the program word and the verb slot.</summary>
    public const int FirstOperandWord = 2;

    /// <summary>
    /// Returns true when <paramref name="word"/> names one existing file or
    /// directory, not a link, directly in <paramref name="directory"/>.
    /// </summary>
    /// <param name="word">One command word.</param>
    /// <param name="directory">The absolute host directory of the command.</param>
    /// <param name="path">The path that the word names.</param>
    public static bool NamesEntry(string word, string? directory, out string path)
        => TryFindEntry(word, directory, out path, out var isLink) && !isLink;

    /// <summary>
    /// Returns true when <paramref name="word"/> names one existing link directly
    /// in <paramref name="directory"/>. The word stays a command word, and the
    /// approval matcher adds the link path as a path scope.
    /// </summary>
    /// <param name="word">One command word.</param>
    /// <param name="directory">The absolute host directory of the command.</param>
    /// <param name="path">The path of the link.</param>
    public static bool NamesLink(string word, string? directory, out string path)
        => TryFindEntry(word, directory, out path, out var isLink) && isLink;

    private static bool TryFindEntry(string word, string? directory, out string path, out bool isLink)
    {
        path = string.Empty;
        isLink = false;
        if (string.IsNullOrEmpty(directory)
            || !Path.IsPathFullyQualified(directory)
            || word is "" or "." or ".."
            || !string.Equals(Path.GetFileName(word), word, StringComparison.Ordinal))
        {
            return false;
        }

        path = Path.Join(directory, word);
        try
        {
            isLink = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the words after the verb slot that name an existing file or
    /// directory in <paramref name="directory"/>.
    /// </summary>
    public static IReadOnlyList<string> Find(IReadOnlyList<string> words, string? directory)
        => words.Skip(FirstOperandWord).Where(word => NamesEntry(word, directory, out _)).ToArray();
}
