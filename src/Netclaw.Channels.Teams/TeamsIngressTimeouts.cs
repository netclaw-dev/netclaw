// -----------------------------------------------------------------------
// <copyright file="TeamsIngressTimeouts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Teams;

internal static class TeamsIngressTimeouts
{
    internal static readonly TimeSpan AttachmentOperation = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InlineImageDownload = TimeSpan.FromSeconds(240);
    internal static readonly TimeSpan AttachmentBatch = TimeSpan.FromSeconds(270);
    internal const int ConcurrentAttachments = 3;

    // One batch deadline bounds all downloads and scans below the SDK five-minute activity limit.
    internal static TimeSpan BindingRoute(TeamsInboundActivity activity) =>
        TimeSpan.FromSeconds(10) + (activity.Attachments.Length > 0 ? AttachmentBatch : TimeSpan.Zero);

    internal static TimeSpan ConversationRoute(TeamsInboundActivity activity) =>
        BindingRoute(activity) + TimeSpan.FromSeconds(5);

    internal static TimeSpan IngressRoute(TeamsInboundActivity activity) =>
        ConversationRoute(activity) + TimeSpan.FromSeconds(5);
}

// This exception crosses only the local downloader call. It contains no SDK exception or resource identifier.
internal sealed class TeamsAttachmentDownloadException(
    string hostClass,
    bool authenticated,
    string stage,
    bool cancelled,
    bool httpError,
    bool bodyIdleTimeout) : Exception("The Teams attachment download failed.")
{
    internal string HostClass { get; } = hostClass;
    internal bool Authenticated { get; } = authenticated;
    internal string Stage { get; } = stage;
    internal bool Cancelled { get; } = cancelled;
    internal bool HttpError { get; } = httpError;
    internal bool BodyIdleTimeout { get; } = bodyIdleTimeout;
}
