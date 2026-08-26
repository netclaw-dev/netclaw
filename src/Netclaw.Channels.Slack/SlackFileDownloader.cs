// -----------------------------------------------------------------------
// <copyright file="SlackFileDownloader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Shared helper for downloading files from Slack's private file API with bot token auth.
/// Delegates to <see cref="StreamingAttachmentDownloader"/> for streamed-to-disk downloads.
/// </summary>
internal static class SlackFileDownloader
{
    public static Task<AttachmentDownloadResult> DownloadToFileAsync(
        HttpClient httpClient,
        string url,
        SensitiveString? botToken,
        string targetDirectory,
        long maxBytes,
        CancellationToken cancellationToken,
        Action<Exception, string>? onCleanupFailure = null)
    {
        return StreamingAttachmentDownloader.DownloadToFileAsync(
            httpClient, url,
            request =>
            {
                if (botToken is { Value: { Length: > 0 } token })
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            },
            targetDirectory,
            maxBytes,
            cancellationToken,
            onCleanupFailure);
    }
}
