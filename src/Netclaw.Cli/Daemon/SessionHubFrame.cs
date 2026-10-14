// -----------------------------------------------------------------------
// <copyright file="SessionHubFrame.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using Microsoft.AspNetCore.SignalR.Protocol;
using Netclaw.Actors.Protocol;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Measures the exact SignalR frame a chat message becomes, using the same JSON hub protocol the client's
/// <c>HubConnection</c> uses (default builder options), and compares it with the hub's receive limit.
/// </summary>
internal static class SessionHubFrame
{
    /// <summary>
    /// Frame bytes not known before connecting: a session id longer than <see cref="PlaceholderSessionId"/>.
    /// Ids are <c>signalr/</c> plus 32 hex chars (40 chars); the placeholder is 64, this covers a longer one.
    /// </summary>
    internal const int HeadroomBytes = 256;

    // Largest invocation id the client can produce (a 32-bit counter) and a session id longer than real ones.
    private const string MaxInvocationId = "2147483647";
    private static readonly string PlaceholderSessionId = new('s', 64);
    // Must stay the default JSON protocol: HubConnectionBuilderExtensions builds the client connection with
    // the builder's default protocol, and this measures the bytes that connection will write.
    private static readonly JsonHubProtocol Protocol = new();

    /// <summary>Exact bytes of the <c>SendMessage</c> invocation frame, record separator included.</summary>
    internal static int MeasureSendMessageFrame(string text)
        => Protocol.GetMessageBytes(new InvocationMessage(MaxInvocationId, "SendMessage", [PlaceholderSessionId, text])).Length;

    /// <summary>Returns a one-line error when <paramref name="text"/> would not fit in a hub frame, otherwise null.</summary>
    internal static string? CheckText(string text)
    {
        var size = MeasureSendMessageFrame(text);
        if (size <= SessionHubLimits.MaxReceiveMessageBytes - HeadroomBytes)
            return null;
        // Print the limit actually applied (rounded down) and the size rounded up, one decimal each, so the
        // printed size always exceeds the printed limit.
        var limit = SessionHubLimits.MaxReceiveMessageBytes - HeadroomBytes;
        var limitKb = Math.Floor(limit * 10.0 / 1024) / 10;
        var sizeKb = Math.Ceiling(size * 10.0 / 1024) / 10;
        return $"Message is too large to send ({sizeKb.ToString("0.0", CultureInfo.InvariantCulture)} KB; limit {limitKb.ToString("0.0", CultureInfo.InvariantCulture)} KB).";
    }
}
