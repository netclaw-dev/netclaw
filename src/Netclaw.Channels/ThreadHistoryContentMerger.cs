// -----------------------------------------------------------------------
// <copyright file="ThreadHistoryContentMerger.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using System.Text;

namespace Netclaw.Channels;

public static class ThreadHistoryContentMerger
{
    public static List<AIContent> MergeHistoryWithLiveContents(
        IReadOnlyList<ChannelInput> history,
        IReadOnlyList<AIContent> liveContents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[thread history — messages exchanged before this inbound event]");
        sb.AppendLine();

        var merged = new List<AIContent>();
        var historicalData = new List<AIContent>();

        foreach (var item in history)
        {
            var ts = item.ReceivedAt == default ? string.Empty : $", {item.ReceivedAt:yyyy-MM-dd HH:mm} UTC";
            sb.AppendLine($"<user: {item.SenderId}{ts}>");

            var hasAttachmentAnnouncement = ContainsAttachmentAnnouncement(item.Contents);
            var imageCount = 0;
            foreach (var content in item.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        sb.AppendLine(text.Text);
                        break;

                    case DataContent data:
                        historicalData.Add(data);
                        if (hasAttachmentAnnouncement)
                            break;

                        if (data.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            imageCount++;
                        }
                        else
                        {
                            sb.AppendLine(
                                $"[attachment] mime=\"{AttachmentIngressFormatting.EscapeQuoted(data.MediaType ?? "application/octet-stream")}\" inlined=\"true\"");
                        }

                        break;
                }
            }

            if (imageCount > 0)
                sb.AppendLine($"[image attachments: {imageCount}]");

            sb.AppendLine();
        }

        sb.AppendLine("[end thread history]");

        var liveText = string.Join("\n", liveContents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        var mergedText = string.IsNullOrWhiteSpace(liveText)
            ? sb.ToString()
            : $"{sb}\n\n{liveText}";

        merged.Add(new TextContent(mergedText));
        merged.AddRange(historicalData);

        foreach (var content in liveContents)
        {
            if (content is not TextContent)
                merged.Add(content);
        }

        return merged;
    }

    private static bool ContainsAttachmentAnnouncement(IReadOnlyList<AIContent> contents)
    {
        foreach (var text in contents.OfType<TextContent>())
        {
            var lines = text.Text.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("[attachment]", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
