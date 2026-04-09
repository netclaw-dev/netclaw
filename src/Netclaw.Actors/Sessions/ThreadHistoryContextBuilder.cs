using System.Text;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Assembles backfilled thread messages into a single read-only context block
/// with text and media references that can be injected into session history.
/// </summary>
internal static class ThreadHistoryContextBuilder
{
    public readonly record struct HistoryBlock(string Text, List<SerializableMediaReference>? MediaReferences);

    /// <summary>
    /// Builds a thread history context block from accumulated backfill messages.
    /// Media references from all messages are collected and included inline.
    /// </summary>
    public static HistoryBlock Build(List<SendUserMessage> backfillMessages, string? sessionsBasePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[thread history — messages exchanged before you were mentioned]");
        sb.AppendLine();

        List<SerializableMediaReference>? allMedia = null;

        foreach (var msg in backfillMessages)
        {
            var sender = msg.Source?.SenderId ?? "unknown";
            sb.AppendLine($"<user: {sender}>");

            if (!string.IsNullOrWhiteSpace(msg.Content))
                sb.AppendLine(msg.Content);

            if (msg.MediaReferences is { Count: > 0 })
            {
                allMedia ??= new List<SerializableMediaReference>();
                foreach (var media in msg.MediaReferences)
                {
                    sb.AppendLine($"[image: {media.RelativePath}]");
                    allMedia.Add(media);
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("[end thread history]");

        return new HistoryBlock(sb.ToString(), allMedia);
    }
}
