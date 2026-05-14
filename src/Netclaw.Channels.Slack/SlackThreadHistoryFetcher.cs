// -----------------------------------------------------------------------
// <copyright file="SlackThreadHistoryFetcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Security;
using SlackNet;
using SlackNet.WebApi;
using IOFile = System.IO.File;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Fetches prior messages from a Slack thread via <c>conversations.replies</c>
/// and returns them as <see cref="ChannelInput"/> items in chronological order.
/// Historical attachments reuse the live ingress security pipeline: policy gates,
/// staged download, content scan, and inbox promotion before inclusion.
/// </summary>
public sealed class SlackThreadHistoryFetcher : IThreadHistoryFetcher
{
    private const int PageSize = 200;
    private static readonly TimeSpan FileDownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContentScanTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Thin abstraction over <c>conversations.replies</c> to keep the fetcher testable
    /// without faking the entire <see cref="IConversationsApi"/> surface.
    /// </summary>
    public delegate Task<ConversationMessagesResponse> RepliesFetcher(
        SlackChannelId channelId, SlackThreadTs threadTs, int limit, string? cursor, CancellationToken ct);

    private readonly RepliesFetcher _repliesFetcher;
    private readonly SlackChannelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IContentScanner _contentScanner;
    private readonly NetclawPaths _paths;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly ILogger<SlackThreadHistoryFetcher> _logger;

    public SlackThreadHistoryFetcher(
        RepliesFetcher repliesFetcher,
        SlackChannelOptions options,
        HttpClient httpClient,
        IContentScanner contentScanner,
        NetclawPaths paths,
        ToolAudienceProfiles audienceProfiles,
        ModelCapabilities modelCapabilities,
        ILogger<SlackThreadHistoryFetcher> logger)
    {
        _repliesFetcher = repliesFetcher;
        _options = options;
        _httpClient = httpClient;
        _contentScanner = contentScanner;
        _paths = paths;
        _audienceProfiles = audienceProfiles;
        _modelCapabilities = modelCapabilities;
        _logger = logger;
    }

    /// <summary>
    /// Convenience constructor that wraps an <see cref="IConversationsApi"/> instance.
    /// </summary>
    public SlackThreadHistoryFetcher(
        IConversationsApi conversationsApi,
        SlackChannelOptions options,
        HttpClient httpClient,
        IContentScanner contentScanner,
        NetclawPaths paths,
        ToolAudienceProfiles audienceProfiles,
        ModelCapabilities modelCapabilities,
        ILogger<SlackThreadHistoryFetcher> logger)
        : this(
            (channelId, threadTs, limit, cursor, ct) =>
                conversationsApi.Replies(channelId.Value, threadTs.Value, limit: limit, cursor: cursor, cancellationToken: ct),
            options, httpClient, contentScanner, paths, audienceProfiles, modelCapabilities, logger)
    {
    }

    public async Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var parts = sessionId.Value.Split('/', 2);
        if (parts.Length != 2)
        {
            _logger.LogWarning("Cannot extract channel/thread from session ID {SessionId}", sessionId.Value);
            return [];
        }

        var channelId = new SlackChannelId(parts[0]);
        var threadTs = new SlackThreadTs(parts[1]);

        try
        {
            return await FetchRepliesAsync(sessionId, channelId, threadTs, cancellationToken);
        }
        catch (SlackException ex)
        {
            _logger.LogWarning(ex, "Slack API error fetching thread history for {SessionId}: {Error}", sessionId.Value, ex.ErrorCode);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch thread history for {SessionId}", sessionId.Value);
            return [];
        }
    }

    private async Task<IReadOnlyList<ChannelInput>> FetchRepliesAsync(
        SessionId sessionId,
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        CancellationToken cancellationToken)
    {
        var audienceResult = ResolveHistoricalAudience(channelId, threadTs);
        if (audienceResult.Error is { } audienceError)
        {
            _logger.LogWarning(
                "Invalid Slack audience configuration while fetching history for {ChannelId}/{ThreadTs}: {Error}",
                channelId.Value,
                threadTs.Value,
                audienceError);
            return [];
        }

        var audience = audienceResult.Audience;
        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(_audienceProfiles, audience);
        var attachmentPolicy = profile.ChannelAttachments ?? ChannelAttachmentPolicy.Empty;
        var inlineImages = _modelCapabilities.InputModalities.HasFlag(ModelModality.Image);

        var results = new List<ChannelInput>();
        string? cursor = null;
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(sessionId, _paths.SessionsDirectory);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(sessionId, _paths.SessionsDirectory);

        do
        {
            var response = await _repliesFetcher(
                channelId,
                threadTs,
                PageSize,
                cursor,
                cancellationToken);

            foreach (var message in response.Messages)
            {
                var senderId = !string.IsNullOrWhiteSpace(message.User)
                    ? message.User
                    : !string.IsNullOrWhiteSpace(message.BotId)
                        ? message.BotId
                        : null;

                if (senderId is null)
                    continue;

                // Bot-authored entries are only adopted from server-side
                // history at the thread root. The root is the one position
                // whose content cannot already exist in any session's
                // persisted transcript — by definition no session ran in
                // this thread before the root was posted. Any bot entry
                // below the root was produced by one of our sessions and
                // is already in transcript; re-adopting it from history
                // would surface our own outputs as third-party context
                // (regression observed in issue #955). The cursor
                // watermark is a cost-amortization, not a correctness
                // primitive — it filters by ts, not by author.
                var isBotAuthored = !string.IsNullOrWhiteSpace(message.BotId);
                var isThreadRoot = string.Equals(message.Ts, threadTs.Value, StringComparison.Ordinal);
                if (isBotAuthored && !isThreadRoot)
                    continue;

                var input = await ConvertMessageAsync(
                    message,
                    senderId,
                    channelId,
                    audience,
                    attachmentPolicy,
                    inlineImages,
                    inboxDir,
                    stagingDir,
                    cancellationToken);
                if (input is not null)
                    results.Add(input);
            }

            cursor = response.ResponseMetadata?.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        _logger.LogInformation(
            "Fetched {Count} thread history messages for {ChannelId}/{ThreadTs}",
            results.Count,
            channelId,
            threadTs);
        return results;
    }

    private async Task<ChannelInput?> ConvertMessageAsync(
        SlackNet.Events.MessageEvent message,
        string senderId,
        SlackChannelId channelId,
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

        if (message.Files is { Count: > 0 } files)
        {
            if (files.Count > attachmentPolicy.MaxFilesPerMessage)
            {
                _logger.LogWarning(
                    "Skipping {Count} historical Slack attachments on {ChannelId}; limit is {Limit} for audience {Audience}",
                    files.Count,
                    channelId.Value,
                    attachmentPolicy.MaxFilesPerMessage,
                    audience);
                contents.Add(BuildHistoricalAttachmentRejected(
                    $"{files.Count} historical attachments exceed the {attachmentPolicy.MaxFilesPerMessage} per-message limit"));
            }
            else
            {
                var downloadableFiles = files
                    .Where(f => f.Mimetype is not null
                        && !string.IsNullOrWhiteSpace(f.UrlPrivateDownload ?? f.UrlPrivate))
                    .ToArray();

                var downloadTasks = downloadableFiles.Select(file => DownloadAndProjectFileAsync(
                    file,
                    audience,
                    attachmentPolicy,
                    inlineImages,
                    inboxDir,
                    stagingDir,
                    cancellationToken));
                var results = await Task.WhenAll(downloadTasks);

                foreach (var result in results)
                    contents.AddRange(result);
            }
        }

        if (contents.Count == 0)
            return null;

        var receivedAt = new SlackEventTs(message.Ts ?? string.Empty).ToDateTimeOffset() ?? default;

        return new ChannelInput
        {
            SenderId = senderId,
            ChannelId = channelId.Value,
            MessageId = $"{channelId.Value}:{message.Ts ?? string.Empty}",
            Audience = audience,
            Contents = contents,
            ReceivedAt = receivedAt
        };
    }

    private async Task<IReadOnlyList<AIContent>> DownloadAndProjectFileAsync(
        SlackNet.File file,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var downloadUrl = file.UrlPrivateDownload ?? file.UrlPrivate!;
        var filename = file.Name ?? "attachment";
        var mimeType = file.Mimetype ?? "application/octet-stream";
        var category = AttachmentCategories.FromMime(mimeType);
        var sourceKey = BuildHistoricalAttachmentSourceKey(file, downloadUrl);

        if (!policy.Allows(category))
        {
            _logger.LogWarning(
                "Historical Slack attachment {Name} rejected: category {Category} not allowed for {Audience}",
                filename,
                category,
                audience);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment ({mimeType}) category not allowed in {audience}")];
        }

        if (file.Size > policy.MaxFileBytes)
        {
            _logger.LogWarning(
                "Historical Slack attachment {Name} rejected: size {Size} exceeds {Limit}",
                filename,
                file.Size,
                policy.MaxFileBytes);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" exceeds the {AttachmentIngressFormatting.FormatBytes(policy.MaxFileBytes)} per-file limit")];
        }

        if (HistoricalAttachmentInbox.TryGetExistingFile(inboxDir, filename, sourceKey, out var existingPath, out var existingSize))
            return await BuildAcceptedAttachmentContentsAsync(
                existingPath,
                filename,
                mimeType,
                category,
                inlineImages,
                existingSize,
                cancellationToken);

        AttachmentDownloadResult downloadResult;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(FileDownloadTimeout);

            downloadResult = await SlackFileDownloader.DownloadToFileAsync(
                _httpClient,
                downloadUrl,
                _options.BotToken,
                stagingDir,
                policy.MaxFileBytes,
                downloadCts.Token,
                (ex, path) => _logger.LogError(ex, "Failed to clean up staged download file {Path}", path));
        }
        catch (AttachmentTooLargeException ex)
        {
            _logger.LogWarning(
                "Historical Slack attachment {Name} rejected during download: {Size} exceeds {Limit}",
                filename,
                ex.BytesReceived,
                ex.MaxBytes);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" exceeded the {AttachmentIngressFormatting.FormatBytes(ex.MaxBytes)} per-file limit during download")];
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out downloading historical Slack attachment {Name}", filename);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" timed out during download")];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed downloading historical Slack attachment {Name}", filename);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be downloaded")];
        }

        if (downloadResult.BytesWritten == 0)
        {
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" downloaded as zero bytes")];
        }

        ContentScanResult scanResult;
        try
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCts.CancelAfter(ContentScanTimeout);
            scanResult = await _contentScanner.ScanFileAsync(
                downloadResult.FilePath,
                filename,
                mimeType,
                scanCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Historical Slack attachment scan threw for {Name}", filename);
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be scanned")];
        }

        if (!scanResult.IsAllowed)
        {
            _logger.LogWarning(
                "Historical Slack attachment {Name} rejected by scanner: {Error} {Message}",
                filename,
                scanResult.Error?.ToString(),
                scanResult.Message ?? string.Empty);
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" was rejected by content scanning: {AttachmentIngressFormatting.EscapeQuoted(scanResult.Message ?? scanResult.Error?.ToString() ?? "unknown error")}")];
        }

        string inboxPath;
        try
        {
            inboxPath = HistoricalAttachmentInbox.PromoteOrReuse(
                inboxDir,
                filename,
                sourceKey,
                downloadResult.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to promote historical Slack attachment {Name} into inbox", filename);
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be saved to the session inbox")];
        }

        return await BuildAcceptedAttachmentContentsAsync(
            inboxPath,
            filename,
            mimeType,
            category,
            inlineImages,
            downloadResult.BytesWritten,
            cancellationToken);
    }

    private async Task<IReadOnlyList<AIContent>> BuildAcceptedAttachmentContentsAsync(
        string inboxPath,
        string filename,
        string mimeType,
        AttachmentCategory category,
        bool inlineImages,
        long size,
        CancellationToken cancellationToken)
    {
        var relativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/{Path.GetFileName(inboxPath)}";
        var (inlined, note) = AttachmentIngressFormatting.ResolveInlineDecision(category, inlineImages);
        var line = new TextContent(AttachmentIngressFormatting.BuildAttachmentLine(
            filename,
            mimeType,
            size,
            relativePath,
            inlined,
            note));

        if (!inlined)
        {
            return [line];
        }

        var bytes = await IOFile.ReadAllBytesAsync(inboxPath, cancellationToken);
        return [line, new DataContent(bytes, mimeType)];
    }

    private AudienceResult ResolveHistoricalAudience(SlackChannelId channelId, SlackThreadTs threadTs)
    {
        var isExplicitChannel = _options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);
        var isDirectMessage = channelId.Value.StartsWith("D", StringComparison.Ordinal);

        return AudienceResult.Resolve(
            channelId.Value, isDirectMessage,
            _options.ChannelAudiences,
            isExplicitUser: false,
            isExplicitChannel: isExplicitChannel);
    }

    private static TextContent BuildHistoricalAttachmentRejected(string detail)
        => new($"[attachment rejected: {detail}]");

    private static string BuildHistoricalAttachmentSourceKey(SlackNet.File file, string downloadUrl)
        => !string.IsNullOrWhiteSpace(file.Id)
            ? $"slack:{file.Id}"
            : $"slack-url:{downloadUrl}";
}
