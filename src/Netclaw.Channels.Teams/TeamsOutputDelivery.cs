// -----------------------------------------------------------------------
// <copyright file="TeamsOutputDelivery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Channels.Teams;

public enum TeamsDeliveryStatus
{
    Delivered,
    Updated,
    IgnoredEmpty,
    RejectedTooLarge,
    Unavailable,
    Cancelled,
    Failed,
    InvalidDestination
}

/// <summary>
/// Durable state for one reminder delivery key. A recovered Sending state is
/// represented as DeliveryUnknown and requires operator-visible retry policy;
/// it never implies a successful external post.
/// </summary>
public enum TeamsProactiveDeliveryState
{
    Pending = 0,
    Sending = 1,
    Sent = 2,
    FailedRetryable = 3,
    FailedPermanent = 4,
    DeliveryUnknown = 5
}

public sealed record TeamsDeliveryResult(TeamsDeliveryStatus Status, string? ActivityId = null, string? ReasonCode = null)
{
    public bool IsSuccess => Status is TeamsDeliveryStatus.Delivered or TeamsDeliveryStatus.Updated;
}

/// <summary>
/// The SDK-free delivery port. The daemon transport is the only implementation
/// that can perform Teams SDK calls.
/// </summary>
public interface ITeamsReplyClient
{
    Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a transient native Teams typing indication. Unlike a message,
    /// this has no durable activity identifier and must never gate delivery
    /// of the eventual response.
    /// </summary>
    Task<TeamsDeliveryResult> SendTypingAsync(
        TeamsOutboundDestination destination,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces ordered Teams text payloads. The byte ceiling covers the serialized
/// activity envelope, not only user-visible characters.
/// </summary>
public sealed class TeamsOutputRenderer
{
    public const int MaxSerializedPayloadBytes = 80 * 1024;
    public const int MaxChunkCount = 16;

    public TeamsRenderedOutput Render(string? text, string? replyToActivityId = null)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
            return TeamsRenderedOutput.Empty;

        if (GetSerializedPayloadBytes(normalized, replyToActivityId) <= MaxSerializedPayloadBytes)
            return new TeamsRenderedOutput([normalized], false);

        if (Encoding.UTF8.GetByteCount(normalized) > MaxSerializedPayloadBytes * MaxChunkCount)
            return TeamsRenderedOutput.TooLarge;

        var chunks = new List<string>();
        var offset = 0;
        while (offset < normalized.Length && chunks.Count < MaxChunkCount)
        {
            var length = FindChunkLength(normalized, offset, replyToActivityId);
            if (length == 0)
                return TeamsRenderedOutput.TooLarge;

            chunks.Add(normalized.Substring(offset, length));
            offset += length;
        }

        return offset == normalized.Length
            ? new TeamsRenderedOutput(chunks, false)
            : TeamsRenderedOutput.TooLarge;
    }

    internal static int GetSerializedPayloadBytes(string text, string? replyToActivityId = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new TeamsTextPayload(text, replyToActivityId)).Length;

    private static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static int FindChunkLength(string value, int offset, string? replyToActivityId)
    {
        var safeBoundary = -1;
        var fallbackBoundary = -1;
        var linkTextOpen = false;
        var linkUrlOpen = false;
        var escaped = false;
        var index = offset;

        while (index < value.Length)
        {
            var next = NextTextElement(value, index);
            if (GetSerializedPayloadBytes(value.Substring(offset, next - offset), replyToActivityId) > MaxSerializedPayloadBytes)
                break;

            for (var characterIndex = index; characterIndex < next; characterIndex++)
            {
                var character = value[characterIndex];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (!linkUrlOpen && character == '[')
                    linkTextOpen = true;
                else if (linkTextOpen && character == ']'
                         && characterIndex + 1 < value.Length
                         && value[characterIndex + 1] == '(')
                {
                    linkTextOpen = false;
                    linkUrlOpen = true;
                }
                else if (linkUrlOpen && character == ')')
                    linkUrlOpen = false;
            }

            if (!linkTextOpen && !linkUrlOpen && char.IsWhiteSpace(value[next - 1]))
                safeBoundary = next;
            fallbackBoundary = next;
            index = next;
        }

        if (index == value.Length)
            return index - offset;

        if (safeBoundary > offset)
            return safeBoundary - offset;

        return linkTextOpen || linkUrlOpen || fallbackBoundary <= offset
            ? 0
            : fallbackBoundary - offset;
    }

    private static int NextTextElement(string value, int index)
    {
        var next = index + 1;
        if (char.IsHighSurrogate(value[index])
            && next < value.Length
            && char.IsLowSurrogate(value[next]))
        {
            next++;
        }

        while (next < value.Length)
        {
            if (char.GetUnicodeCategory(value[next]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                next++;
                continue;
            }

            if (value[next] != '\u200D')
                break;

            next++;
            if (next >= value.Length)
                break;

            next++;
            if (char.IsHighSurrogate(value[next - 1])
                && next < value.Length
                && char.IsLowSurrogate(value[next]))
            {
                next++;
            }
        }

        return next;
    }

    private sealed record TeamsTextPayload(string Text, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyToId)
    {
        public string Type => "message";
        public string TextFormat => "markdown";
    }
}

public sealed record TeamsRenderedOutput(IReadOnlyList<string> Chunks, bool IsRejectedTooLarge)
{
    public static readonly TeamsRenderedOutput Empty = new([], false);
    public static readonly TeamsRenderedOutput TooLarge = new([], true);
}
