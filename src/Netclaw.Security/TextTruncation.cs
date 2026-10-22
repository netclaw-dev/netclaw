// -----------------------------------------------------------------------
// <copyright file="TextTruncation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Shared text-truncation helpers for log previews and prompt snippets.
/// <c>Netclaw.Actors.Skills.SkillScanner</c> has its own truncation that fits
/// an ellipsis *within* the byte budget; this helper instead appends an
/// ellipsis after the budget. The two semantics are intentionally distinct.
/// This type lives in Netclaw.Security so that <see cref="ToolCallLogFormatter"/>
/// and the session log actors use one cap rule.
/// </summary>
public static class TextTruncation
{
    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it fits within
    /// <paramref name="maxLength"/> characters; otherwise returns the first
    /// <paramref name="maxLength"/> characters with an ellipsis appended
    /// (so the result is at most <c>maxLength + 3</c> characters long).
    /// </summary>
    public static string EllipsisAppend(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");
}
