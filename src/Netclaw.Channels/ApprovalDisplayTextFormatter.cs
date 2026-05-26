// -----------------------------------------------------------------------
// <copyright file="ApprovalDisplayTextFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Shrinks the raw command shown in an approval prompt so it fits inside
/// per-channel transport size caps. When a chat platform rejects an
/// oversized post the channel binding actor falls back to auto-denying
/// (see each adapter's <c>SendApprovalDenyOnFailureAsync</c>) — keeping
/// the prompt within bounds is what prevents that fallback from firing.
/// </summary>
public static class ApprovalDisplayTextFormatter
{
    /// <summary>
    /// Marker inserted in place of the elided middle. Includes a length hint
    /// so reviewers know the visible text is a summary, not the full
    /// command.
    /// </summary>
    public const string TruncationMarker = " … [truncated, original {0} chars] … ";

    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it already fits inside
    /// <paramref name="maxChars"/>. Otherwise returns a middle-elided form
    /// no longer than <paramref name="maxChars"/>, with the original
    /// character count rendered into the marker.
    /// </summary>
    public static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
            return string.Empty;

        if (text.Length <= maxChars)
            return text;

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
            marker,
            text.AsSpan(text.Length - tail, tail));
    }
}
