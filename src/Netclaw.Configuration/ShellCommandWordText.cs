// -----------------------------------------------------------------------
// <copyright file="ShellCommandWordText.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The canonical phrase text of shell command words. A grant and a call use
/// this one rule.
/// </summary>
/// <remarks>
/// <para>
/// A command word is the static value of a word after shell quote removal. So
/// <c>"my tool"</c>, <c>'my tool'</c>, and <c>my\ tool</c> give one Bash word,
/// <c>my tool</c>. A word can contain a space, for example the path
/// <c>/opt/My App/bin/tool</c>. A grant compares the word list, not a text.
/// </para>
/// <para>
/// SECURITY: the phrase text of a word list is also a policy input. The side
/// effect verb set and the single-token verb rule read its first word. So
/// <see cref="FormatPhrase"/> quotes each word that contains whitespace. Then
/// the program <c>"echo x"</c> gives the phrase <c>'echo x'</c>, not the
/// phrase <c>echo x</c> of the program <c>echo</c>. A word without whitespace
/// keeps its text, so stored phrases do not change.
/// </para>
/// <para>
/// Owner: the shell candidate builder, the grant parser, and the approval
/// store. The data is call-local. The store keeps the word list, which is
/// durable.
/// </para>
/// </remarks>
public static class ShellCommandWordText
{
    /// <summary>
    /// Returns the text of one word in <paramref name="shell"/>. A word with
    /// whitespace is in single quotes. Other words do not change.
    /// </summary>
    public static string Quote(ApprovalShell shell, string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (!word.Any(char.IsWhiteSpace))
            return word;

        var escapedQuote = shell switch
        {
            ApprovalShell.Bash => "'\\''",
            ApprovalShell.PowerShell => "''",
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unknown shell."),
        };
        return "'" + word.Replace("'", escapedQuote, StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Returns the phrase text of a word list: each word in the text of
    /// <paramref name="shell"/>, with one space between.
    /// </summary>
    public static string FormatPhrase(ApprovalShell shell, IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return string.Join(' ', words.Select(word => Quote(shell, word)));
    }
}
