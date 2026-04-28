using System.Text;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;

namespace Netclaw.Channels;

public enum AdoptedMessageAuthority
{
    Authorized,
    Pending
}

public sealed record AdoptedContextMessage(
    ChannelInput Input,
    AdoptedMessageAuthority AuthorityAtInclusion);

public static class AdoptedContextContentBuilder
{
    private static readonly string[] ReservedMarkerPrefixes =
    [
        "[adopted-context]",
        "[/adopted-context]",
        "[adopted-message ",
        "[/adopted-message]",
        "[current-authorized-message ",
        "[/current-authorized-message]"
    ];

    public static List<AIContent> MergeWithCurrentMessage(
        IReadOnlyList<AdoptedContextMessage> adopted,
        IReadOnlyList<AIContent> liveContents,
        string currentAuthorId,
        DateTimeOffset currentReceivedAt)
    {
        if (adopted.Count == 0)
            return [.. liveContents];

        var merged = new List<AIContent>();
        var adoptedData = new List<AIContent>();
        var text = new StringBuilder();

        text.AppendLine("[adopted-context]");

        foreach (var item in adopted)
        {
            var messageId = EscapeAttribute(item.Input.MessageId ?? "unknown");
            var senderId = EscapeAttribute(item.Input.SenderId);
            var authority = item.AuthorityAtInclusion == AdoptedMessageAuthority.Authorized
                ? "authorized"
                : "pending";
            var ts = item.Input.ReceivedAt == default
                ? string.Empty
                : $" ts={item.Input.ReceivedAt:O}";

            text.AppendLine($"[adopted-message id={messageId} author={senderId} authority-at-inclusion={authority}{ts}]");

            var hasAttachmentAnnouncement = ContainsAttachmentAnnouncement(item.Input.Contents);
            var imageCount = 0;
            foreach (var content in item.Input.Contents)
            {
                switch (content)
                {
                    case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                        foreach (var line in EscapeReservedMarkerLines(textContent.Text).Split('\n'))
                            text.AppendLine(line);
                        break;

                    case DataContent data:
                        adoptedData.Add(data);
                        if (hasAttachmentAnnouncement)
                            break;

                        if (data.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            imageCount++;
                        }
                        else
                        {
                            text.AppendLine(
                                $"[attachment] mime=\"{AttachmentIngressFormatting.EscapeQuoted(data.MediaType ?? "application/octet-stream")}\" inlined=\"true\"");
                        }

                        break;
                }
            }

            if (imageCount > 0)
                text.AppendLine($"[image attachments: {imageCount}]");

            text.AppendLine("[/adopted-message]");
        }

        text.AppendLine("[/adopted-context]");

        var liveText = string.Join("\n", liveContents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        var currentTs = currentReceivedAt == default
            ? string.Empty
            : $" ts={currentReceivedAt:O}";
        text.AppendLine($"[current-authorized-message author={EscapeAttribute(currentAuthorId)}{currentTs}]");
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            foreach (var line in EscapeReservedMarkerLines(liveText).Split('\n'))
                text.AppendLine(line);
        }
        text.AppendLine("[/current-authorized-message]");

        merged.Add(new TextContent(text.ToString().TrimEnd()));
        merged.AddRange(adoptedData);

        foreach (var content in liveContents)
        {
            if (content is not TextContent)
                merged.Add(content);
        }

        return merged;
    }

    private static string EscapeReservedMarkerLines(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var prefix in ReservedMarkerPrefixes)
            {
                if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    lines[i] = $"\\{lines[i]}";
                    break;
                }
            }
        }

        return string.Join("\n", lines);
    }

    private static string EscapeAttribute(string value)
        => value.Replace(" ", "_", StringComparison.Ordinal);

    private static bool ContainsAttachmentAnnouncement(IReadOnlyList<AIContent> contents)
    {
        foreach (var text in contents.OfType<TextContent>())
        {
            foreach (var line in text.Text.Split('\n'))
            {
                if (line.StartsWith("[attachment]", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
