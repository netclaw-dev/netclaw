// -----------------------------------------------------------------------
// <copyright file="MessageChunker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Splits an outgoing message into transport-sized chunks. A split point
/// prefers the last newline inside the length budget, so a chunk break
/// does not cut a line in half when the text has line structure.
/// </summary>
public static class MessageChunker
{
    public static List<string> Chunk(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return [text];

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining.ToString());
                break;
            }

            var splitAt = maxLength;
            var newlineIdx = remaining[..splitAt].LastIndexOf('\n');
            if (newlineIdx > 0)
                splitAt = newlineIdx + 1;

            chunks.Add(remaining[..splitAt].ToString());
            remaining = remaining[splitAt..];
        }

        return chunks;
    }
}
