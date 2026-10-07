// -----------------------------------------------------------------------
// <copyright file="ApprovalDisplayTextFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Shrinks the text of an approval prompt so it fits inside per-channel
/// transport size caps. When a chat platform rejects an oversized post, the
/// channel binding cannot show the prompt and the call does not run. Each
/// variable field of a prompt (the command, a candidate verb, a directory, a
/// speaker list) goes through this helper with a field budget.
/// </summary>
/// <remarks>
/// A candidate verb can be the full source text of one command. ShellSyntaxTree
/// splits a heredoc inside a command substitution into commands, and an
/// unresolved command becomes one exact candidate whose verb is its text. So a
/// verb is not always short, and every builder must bound it.
/// </remarks>
public static class ApprovalDisplayTextFormatter
{
    /// <summary>
    /// Marker inserted in place of the elided middle. It states how many
    /// characters the prompt does not show, so the reader knows that the
    /// visible text is a summary, not the full command.
    /// </summary>
    public const string TruncationMarker = " … {0} characters hidden … ";

    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it already fits inside
    /// <paramref name="maxChars"/>. Otherwise returns the head and the tail of
    /// the text, no longer than <paramref name="maxChars"/>, with the number of
    /// hidden characters in the marker between them.
    /// </summary>
    public static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
            return string.Empty;

        if (text.Length <= maxChars)
            return text;

        // The hidden count depends on the marker length, and the marker length
        // depends on the digits of the count. Size the marker with the largest
        // count it can show, so the result never exceeds the budget.
        var marker = string.Format(TruncationMarker, text.Length);

        // Budget smaller than the marker itself: drop the marker and use a
        // plain "…" so the prompt still posts.
        if (maxChars <= marker.Length)
            return string.Concat(text.AsSpan(0, maxChars - 1), "…");

        var keep = maxChars - marker.Length;
        var head = keep / 2;
        var tail = keep - head;

        return string.Concat(
            text.AsSpan(0, head),
            string.Format(TruncationMarker, text.Length - keep),
            text.AsSpan(text.Length - tail, tail));
    }

    /// <summary>
    /// Bounds a list of prompt items, for example candidate verbs. Each item
    /// is shortened to <paramref name="maxItemChars"/>. The items together,
    /// with <paramref name="perItemOverhead"/> characters of layout each, stay
    /// within <paramref name="maxTotalChars"/>. When an item does not fit, the
    /// list stops and its last entry tells how many items it does not show.
    /// </summary>
    public static IReadOnlyList<string> TruncateList(
        IReadOnlyList<string> items,
        int maxItemChars,
        int maxTotalChars,
        int perItemOverhead)
    {
        var result = new List<string>(items.Count);
        var used = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = Truncate(items[i], maxItemChars);
            var remaining = items.Count - i - 1;

            // Keep room for the "more" line unless this is the last item.
            var reserve = remaining > 0 ? MoreItemsLine(remaining).Length + perItemOverhead : 0;
            if (used + item.Length + perItemOverhead + reserve > maxTotalChars)
            {
                result.Add(MoreItemsLine(items.Count - i));
                return result;
            }

            result.Add(item);
            used += item.Length + perItemOverhead;
        }

        return result;
    }

    private static string MoreItemsLine(int count)
        => $"… {count} more not shown";
}
