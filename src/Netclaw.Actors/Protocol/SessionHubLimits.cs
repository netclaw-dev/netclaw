// -----------------------------------------------------------------------
// <copyright file="SessionHubLimits.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Size limit for a single SignalR frame on the session hub, shared by the daemon (which enforces it)
/// and the CLI (which refuses over-limit chat text before sending, so the connection is not dropped).
/// </summary>
public static class SessionHubLimits
{
    /// <summary>Hub <c>MaximumReceiveMessageSize</c>: 512 KB for the whole frame (JSON envelope plus escaped text).</summary>
    public const int MaxReceiveMessageBytes = 512 * 1024;

    /// <summary>
    /// Reserved for the invocation envelope around the text: invocation id, target name, session id
    /// and the record separator. The real envelope is well under 300 bytes; 1 KB leaves a wide margin.
    /// </summary>
    public const int EnvelopeHeadroomBytes = 1024;

    /// <summary>
    /// Bytes the text occupies inside the JSON frame. The default encoder escapes at least everything the
    /// hub protocol's encoder does (quotes, backslashes, control characters, non-ASCII), so this is an
    /// upper bound on the text's contribution to the frame.
    /// </summary>
    public static int MeasureEncodedTextBytes(string text)
        => JsonEncodedText.Encode(text, JavaScriptEncoder.Default).EncodedUtf8Bytes.Length;

    /// <summary>
    /// Returns a one-line error when <paramref name="text"/> would not fit in a hub frame, otherwise null.
    /// </summary>
    public static string? CheckText(string text)
    {
        var size = MeasureEncodedTextBytes(text);
        if (size <= MaxReceiveMessageBytes - EnvelopeHeadroomBytes)
            return null;
        return $"Message is too large to send ({(size + 1023) / 1024} KB; limit {MaxReceiveMessageBytes / 1024} KB).";
    }
}
