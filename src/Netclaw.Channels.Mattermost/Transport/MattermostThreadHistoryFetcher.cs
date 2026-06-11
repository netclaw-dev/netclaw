// -----------------------------------------------------------------------
// <copyright file="MattermostThreadHistoryFetcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
using Mattermost.Models;
using Mattermost.Models.Posts;
using Mattermost.Models.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;
using IOFile = System.IO.File;

namespace Netclaw.Channels.Mattermost.Transport;

public sealed class MattermostThreadHistoryFetcher : IThreadHistoryFetcher
{
    private static readonly TimeSpan FileDownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContentScanTimeout = TimeSpan.FromSeconds(5);

    internal sealed record HistoricalMessage(
        string MessageId,
        SenderId SenderId,
        bool IsBot,
        string Text,
        DateTimeOffset Timestamp,
        IReadOnlyList<MattermostFileReference> Attachments);

    internal delegate Task<IReadOnlyList<HistoricalMessage>> MessageFetcher(
        string rootPostId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a file by its Mattermost file ID and writes it to the staging directory.
    /// Returns the staging file path and byte count, or null on failure.
    /// </summary>
    internal delegate Task<(string FilePath, long BytesWritten)?> FileDownloader(
        string fileId,
        string stagingDir,
        long maxBytes,
        CancellationToken cancellationToken);

    private readonly MessageFetcher _messageFetcher;
    private readonly FileDownloader _fileDownloader;
    private readonly IContentScanner _contentScanner;
    private readonly MattermostChannelOptions _options;
    private readonly string _serverUrl;
    private readonly string? _botUserId;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly NetclawPaths _paths;
    private readonly ILogger<MattermostThreadHistoryFetcher> _logger;

    public MattermostThreadHistoryFetcher(
        MattermostClient client,
        IContentScanner contentScanner,
        MattermostChannelOptions options,
        string serverUrl,
        Func<string?> botUserIdFactory,
        ToolAudienceProfiles audienceProfiles,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths,
        ILogger<MattermostThreadHistoryFetcher> logger)
        : this(
            (rootPostId, cancellationToken) => FetchRawMessagesAsync(client, rootPostId, botUserIdFactory(), serverUrl, cancellationToken, logger),
            (fileId, stagingDir, maxBytes, ct) => DownloadFileViaSdkAsync(client, fileId, stagingDir, maxBytes, ct),
            contentScanner,
            options,
            serverUrl,
            botUserIdFactory(), // safe: ConnectAsync resolves BotUserId before this constructor runs
            audienceProfiles,
            modelCapabilities,
            paths,
            logger)
    {
    }

    internal MattermostThreadHistoryFetcher(
        MessageFetcher messageFetcher,
        FileDownloader fileDownloader,
        IContentScanner contentScanner,
        MattermostChannelOptions options,
        string serverUrl,
        string? botUserId,
        ToolAudienceProfiles audienceProfiles,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths,
        ILogger<MattermostThreadHistoryFetcher> logger)
    {
        _messageFetcher = messageFetcher;
        _fileDownloader = fileDownloader;
        _contentScanner = contentScanner;
        _options = options;
        _serverUrl = serverUrl.TrimEnd('/');
        _botUserId = botUserId;
        _audienceProfiles = audienceProfiles;
        _modelCapabilities = modelCapabilities;
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!MattermostGatewayActor.TryParseMattermostSessionId(sessionId, out var channelId, out var rootPostId))
        {
            _logger.LogWarning("Cannot extract channel/thread from session ID {SessionId}", sessionId.Value);
            return [];
        }

        var audienceResult = ResolveHistoricalAudience(channelId);
        if (audienceResult.Error is { } audienceError)
        {
            _logger.LogWarning(
                "Invalid Mattermost audience configuration while fetching history for {SessionId}: {Error}",
                sessionId.Value,
                audienceError);
            return [];
        }

        var audience = audienceResult.Audience;
        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(_audienceProfiles, audience);
        var attachmentPolicy = profile.ChannelAttachments ?? ChannelAttachmentPolicy.Empty;
        var inlineImages = _modelCapabilities.InputModalities.HasFlag(ModelModality.Image);
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(sessionId, _paths.SessionsDirectory);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(sessionId, _paths.SessionsDirectory);

        try
        {
            var history = await _messageFetcher(rootPostId.Value, cancellationToken);
            var results = new List<ChannelInput>(history.Count);

            foreach (var message in history)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Bot-authored entries are only adopted from server-side
                // history at the thread root. The root is the one position
                // whose content cannot already exist in any session's
                // persisted transcript — by definition no session ran in
                // this thread before the root was posted. Any bot entry
                // below the root was produced by one of our sessions and
                // is already in transcript; re-adopting it from history
                // would surface our own outputs as third-party context.
                // For Mattermost, the thread root is the post whose id
                // equals the session's root post id.
                var isThreadRoot = string.Equals(message.MessageId, rootPostId.Value, StringComparison.Ordinal);
                if (message.IsBot && !isThreadRoot)
                    continue;

                var input = await ConvertMessageAsync(
                    message,
                    channelId,
                    rootPostId,
                    audience,
                    attachmentPolicy,
                    inlineImages,
                    inboxDir,
                    stagingDir,
                    cancellationToken);
                if (input is not null)
                    results.Add(input);
            }

            _logger.LogInformation(
                "Fetched {Count} thread history messages for Mattermost thread {RootPostId}",
                results.Count, rootPostId.Value);
            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch thread history for {SessionId}", sessionId.Value);
            return [];
        }
    }

    private async Task<ChannelInput?> ConvertMessageAsync(
        HistoricalMessage message,
        MattermostChannelId channelId,
        MattermostRootPostId rootPostId,
        TrustAudience audience,
        ChannelAttachmentPolicy attachmentPolicy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();

        if (!string.IsNullOrWhiteSpace(message.Text))
            contents.Add(new TextContent(message.Text));

        if (message.Attachments.Count > 0)
        {
            if (message.Attachments.Count > attachmentPolicy.MaxFilesPerMessage)
            {
                _logger.LogWarning(
                    "Skipping {Count} historical attachments on thread {RootPostId}; limit is {Limit} for audience {Audience}",
                    message.Attachments.Count,
                    rootPostId.Value,
                    attachmentPolicy.MaxFilesPerMessage,
                    audience);
                contents.Add(BuildHistoricalAttachmentRejected(
                    $"{message.Attachments.Count} historical attachments exceed the {attachmentPolicy.MaxFilesPerMessage} per-message limit"));
            }
            else
            {
                var attachmentTasks = message.Attachments.Select(file => DownloadAndProjectAttachmentAsync(
                    message.MessageId,
                    file,
                    audience,
                    attachmentPolicy,
                    inlineImages,
                    inboxDir,
                    stagingDir,
                    cancellationToken));
                var attachmentResults = await Task.WhenAll(attachmentTasks);

                foreach (var result in attachmentResults)
                    contents.AddRange(result);
            }
        }

        if (contents.Count == 0)
            return null;

        return new ChannelInput
        {
            SenderId = message.SenderId,
            ChannelId = channelId.Value,
            MessageId = message.MessageId,
            Audience = audience,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance(
                TransportAuthenticity.Verified,
                PayloadTaint.Public)
            {
                SourceKind = new SourceKind("mattermost"),
                SourceScope = new SourceScope(rootPostId.Value)
            },
            Contents = contents,
            ReceivedAt = message.Timestamp,
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                ChannelType.Mattermost.ToWireValue(),
                "destination",
                channelId.Value,
                channelId.Value,
                rootPostId.Value)
        };
    }

    private async Task<IReadOnlyList<AIContent>> DownloadAndProjectAttachmentAsync(
        string messageId,
        MattermostFileReference file,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var declaredMimeType = new DeclaredMimeType(file.MimeType);
        var sourceKey = BuildHistoricalAttachmentSourceKey(messageId, file);

        if (HistoricalAttachmentIngress.CheckPreDownload(file.Name, declaredMimeType, file.Size, audience, policy, _logger) is { } preReject)
            return [preReject];

        if (HistoricalAttachmentInbox.TryGetExistingFile(inboxDir, file.Name, sourceKey, out var existingPath, out var existingSize))
        {
            // Re-scan the cached file so a cache hit goes through the same
            // verified-MIME/verified-category gate as a fresh download — never
            // serve the unverified declared MIME.
            var cached = await HistoricalAttachmentIngress.ScanAndVerifyAsync(
                _contentScanner, existingPath, file.Name, declaredMimeType,
                audience, policy, ContentScanTimeout, _logger, cancellationToken);
            return cached is HistoricalAttachmentIngress.ScanOutcome.Verified cachedOk
                ? await AttachmentIngressFormatting.BuildAcceptedContentsAsync(
                    existingPath, file.Name, cachedOk.MimeType.Value, cachedOk.Category,
                    inlineImages, existingSize, cancellationToken)
                : [((HistoricalAttachmentIngress.ScanOutcome.Rejected)cached).Note];
        }

        if (!MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(file.Url, _serverUrl))
        {
            _logger.LogWarning(
                "Historical attachment {Name} rejected: untrusted URL {Url}",
                file.Name,
                file.Url);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" has an untrusted download URL")];
        }

        // Extract file ID from the URL for SDK-based download.
        var fileId = ExtractFileId(file.Url);
        if (fileId is null)
        {
            _logger.LogWarning(
                "Historical attachment {Name} rejected: could not extract file ID from URL {Url}",
                file.Name, file.Url);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" has an unrecognized URL format")];
        }

        (string FilePath, long BytesWritten)? downloadResult;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(FileDownloadTimeout);
            downloadResult = await _fileDownloader(fileId, stagingDir, policy.MaxFileBytes, downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out downloading historical attachment {Name}", file.Name);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" timed out during download")];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed downloading historical attachment {Name}", file.Name);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" could not be downloaded")];
        }

        if (downloadResult is null || downloadResult.Value.BytesWritten == 0)
        {
            if (downloadResult is not null)
                AttachmentStagingCleanup.TryDelete(downloadResult.Value.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" downloaded as zero bytes")];
        }

        var (stagedPath, bytesWritten) = downloadResult.Value;

        if (bytesWritten > policy.MaxFileBytes)
        {
            _logger.LogWarning(
                "Historical attachment {Name} rejected during download: {Size} exceeds {Limit}",
                file.Name, bytesWritten, policy.MaxFileBytes);
            AttachmentStagingCleanup.TryDelete(stagedPath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" exceeded the {AttachmentIngressFormatting.FormatBytes(policy.MaxFileBytes)} per-file limit during download")];
        }

        var scanOutcome = await HistoricalAttachmentIngress.ScanAndVerifyAsync(
            _contentScanner, stagedPath, file.Name, declaredMimeType,
            audience, policy, ContentScanTimeout, _logger, cancellationToken);
        if (scanOutcome is HistoricalAttachmentIngress.ScanOutcome.Rejected rejected)
        {
            AttachmentStagingCleanup.TryDelete(stagedPath, _logger);
            return [rejected.Note];
        }

        var verified = (HistoricalAttachmentIngress.ScanOutcome.Verified)scanOutcome;
        var verifiedMime = verified.MimeType;
        var verifiedCategory = verified.Category;

        string inboxPath;
        try
        {
            inboxPath = HistoricalAttachmentInbox.PromoteOrReuse(
                inboxDir,
                file.Name,
                sourceKey,
                stagedPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to promote historical attachment {Name} into inbox", file.Name);
            AttachmentStagingCleanup.TryDelete(stagedPath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" could not be saved to the session inbox")];
        }

        return await AttachmentIngressFormatting.BuildAcceptedContentsAsync(
            inboxPath,
            file.Name,
            verifiedMime.Value,
            verifiedCategory,
            inlineImages,
            bytesWritten,
            cancellationToken);
    }

    private AudienceResult ResolveHistoricalAudience(MattermostChannelId channelId)
    {
        // DM detection is not available from thread history context, so default to false.
        var isExplicitChannel = _options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);

        return AudienceResult.Resolve(
            channelId.Value, isDirectMessage: false,
            _options.ChannelAudiences,
            isExplicitUser: false,
            isExplicitChannel: isExplicitChannel);
    }

    private static async Task<IReadOnlyList<HistoricalMessage>> FetchRawMessagesAsync(
        MattermostClient client,
        string rootPostId,
        string? botUserId,
        string serverUrl,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        ChannelPostsResponse threadResponse;
        try
        {
            threadResponse = await client.GetThreadPostsAsync(rootPostId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Mattermost thread posts for root {RootPostId}", rootPostId);
            return [];
        }

        if (threadResponse.Posts.Count == 0)
        {
            logger.LogDebug("Thread {RootPostId} returned no posts", rootPostId);
            return [];
        }

        var results = new List<HistoricalMessage>(threadResponse.Order.Count);
        var normalizedServerUrl = serverUrl.TrimEnd('/');

        // Order list is provided by the API in chronological order
        foreach (var postId in threadResponse.Order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!threadResponse.Posts.TryGetValue(postId, out var post))
                continue;

            if (post.DeletedAt > 0)
                continue;

            var isBotMessage = botUserId is not null
                && string.Equals(post.UserId, botUserId, StringComparison.Ordinal);

            // Bot messages are NOT dropped here — FetchThreadHistoryAsync keeps
            // the bot-authored thread root and drops bot entries below it.
            if (!HasUsableContent(post))
                continue;

            var attachments = await ResolveFileReferencesAsync(client, post.FileIdentifiers, normalizedServerUrl, logger);
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(post.CreatedAt);

            results.Add(new HistoricalMessage(
                MessageId: post.Id,
                SenderId: new SenderId(post.UserId),
                IsBot: isBotMessage,
                Text: post.Text ?? string.Empty,
                Timestamp: timestamp,
                Attachments: attachments));
        }

        return results;
    }

    private static async Task<IReadOnlyList<MattermostFileReference>> ResolveFileReferencesAsync(
        MattermostClient client, IList<string> fileIds, string serverUrl, ILogger logger)
    {
        if (fileIds.Count == 0)
            return [];

        var tasks = fileIds.Select(async fileId =>
        {
            try
            {
                var details = await client.GetFileDetailsAsync(fileId);
                return new MattermostFileReference(
                    Name: details.Name ?? fileId,
                    MimeType: details.MimeType ?? "application/octet-stream",
                    Size: details.Size,
                    Url: $"{serverUrl}/api/v4/files/{fileId}");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to resolve file details for {FileId}; using fallback metadata", fileId);
                return new MattermostFileReference(
                    Name: fileId,
                    MimeType: "application/octet-stream",
                    Size: 0,
                    Url: $"{serverUrl}/api/v4/files/{fileId}");
            }
        });

        return await Task.WhenAll(tasks);
    }

    private static async Task<(string FilePath, long BytesWritten)?> DownloadFileViaSdkAsync(
        MattermostClient client,
        string fileId,
        string stagingDir,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = await client.GetFileStreamAsync(fileId);
        var stagingPath = Path.Combine(stagingDir, $"{Guid.NewGuid():N}.tmp");
        long totalBytes = 0;

        try
        {
            await using var fileStream = new FileStream(
                stagingPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytes += bytesRead;
                if (totalBytes > maxBytes)
                {
                    // Exceeded size limit during streaming download
                    await fileStream.DisposeAsync();
                    IOFile.Delete(stagingPath);
                    return null;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        catch
        {
            IOFile.Delete(stagingPath);
            throw;
        }

        return (stagingPath, totalBytes);
    }

    /// <summary>
    /// Extracts the Mattermost file ID from a <c>/api/v4/files/{fileId}</c> URL.
    /// </summary>
    internal static string? ExtractFileId(string url)
    {
        const string marker = "/api/v4/files/";
        var idx = url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var start = idx + marker.Length;
        if (start >= url.Length)
            return null;

        // File ID runs until the next '/' or '?' or end of string
        var end = url.IndexOfAny(['/', '?'], start);
        var fileId = end < 0 ? url[start..] : url[start..end];
        return string.IsNullOrEmpty(fileId) ? null : fileId;
    }

    private static bool HasUsableContent(Post post)
        => !string.IsNullOrWhiteSpace(post.Text) || post.FileIdentifiers.Count > 0;

    private static TextContent BuildHistoricalAttachmentRejected(string detail)
        => HistoricalAttachmentIngress.BuildRejected(detail);

    private static string BuildHistoricalAttachmentSourceKey(string messageId, MattermostFileReference file)
        => $"mattermost:{messageId}:{file.Url}";
}
