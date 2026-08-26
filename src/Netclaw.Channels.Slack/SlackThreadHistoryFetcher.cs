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
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;
using SlackNet;
using SlackNet.WebApi;

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
            _logger.LogWarning(ex, "API error fetching thread history for {SessionId}: {Error}", sessionId.Value, ex.ErrorCode);
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

                var trustResult = ResolveHistoricalTrust(channelId, senderId);
                if (trustResult.Error is { } audienceError)
                {
                    _logger.LogWarning(
                        "Invalid Slack audience configuration while fetching history for {ChannelId}/{ThreadTs}: {Error}",
                        channelId.Value,
                        threadTs.Value,
                        audienceError);
                    return [];
                }

                var attachmentPolicy = ToolAudienceProfileDefaults
                    .GetResolvedProfile(_audienceProfiles, trustResult.Audience)
                    .ChannelAttachments ?? ChannelAttachmentPolicy.Empty;

                var input = await ConvertMessageAsync(
                    message,
                    senderId,
                    channelId,
                    threadTs,
                    trustResult.Audience,
                    trustResult.Principal,
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
        SlackThreadTs threadTs,
        TrustAudience audience,
        PrincipalClassification principal,
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
                    "Skipping {Count} historical attachments on {ChannelId}; limit is {Limit} for audience {Audience}",
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
            SenderId = new Netclaw.Actors.Protocol.SenderId(senderId),
            ChannelId = channelId.Value,
            MessageId = $"{channelId.Value}:{message.Ts ?? string.Empty}",
            Audience = audience,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = principal,
            Provenance = new SourceProvenance(
                TransportAuthenticity.Verified,
                PayloadTaint.Public)
            {
                SourceKind = new SourceKind("slack"),
                SourceScope = new SourceScope(channelId.Value)
            },
            Contents = contents,
            ReceivedAt = receivedAt,
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                Netclaw.Actors.Channels.ChannelType.Slack.ToWireValue(),
                "destination",
                channelId.Value,
                channelId.Value,
                threadTs.Value)
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
        var declaredMimeType = new DeclaredMimeType(file.Mimetype);
        var sourceKey = BuildHistoricalAttachmentSourceKey(file, downloadUrl);

        if (HistoricalAttachmentIngress.CheckPreDownload(filename, declaredMimeType, file.Size, audience, policy, _logger) is { } preReject)
            return [preReject];

        if (HistoricalAttachmentInbox.TryGetExistingFile(inboxDir, filename, sourceKey, out var existingPath, out var existingSize))
        {
            // Re-scan the cached file so a cache hit goes through the same
            // verified-MIME/verified-category gate as a fresh download — never
            // serve the unverified declared MIME.
            var cached = await HistoricalAttachmentIngress.ScanAndVerifyAsync(
                _contentScanner, existingPath, filename, declaredMimeType,
                audience, policy, ContentScanTimeout, _logger, cancellationToken);
            return cached is HistoricalAttachmentIngress.ScanOutcome.Verified cachedOk
                ? await AttachmentIngressFormatting.BuildAcceptedContentsAsync(
                    existingPath, filename, cachedOk.MimeType.Value, cachedOk.Category,
                    inlineImages, existingSize, cancellationToken)
                : [((HistoricalAttachmentIngress.ScanOutcome.Rejected)cached).Note];
        }

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
                "Historical attachment {Name} rejected during download: {Size} exceeds {Limit}",
                filename,
                ex.BytesReceived,
                ex.MaxBytes);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" exceeded the {AttachmentIngressFormatting.FormatBytes(ex.MaxBytes)} per-file limit during download")];
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out downloading historical attachment {Name}", filename);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" timed out during download")];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed downloading historical attachment {Name}", filename);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be downloaded")];
        }

        if (downloadResult.BytesWritten == 0)
        {
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" downloaded as zero bytes")];
        }

        var scanOutcome = await HistoricalAttachmentIngress.ScanAndVerifyAsync(
            _contentScanner, downloadResult.FilePath, filename, declaredMimeType,
            audience, policy, ContentScanTimeout, _logger, cancellationToken);
        if (scanOutcome is HistoricalAttachmentIngress.ScanOutcome.Rejected rejected)
        {
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
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
                filename,
                sourceKey,
                downloadResult.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to promote historical attachment {Name} into inbox", filename);
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(filename)}\" could not be saved to the session inbox")];
        }

        return await AttachmentIngressFormatting.BuildAcceptedContentsAsync(
            inboxPath,
            filename,
            verifiedMime.Value,
            verifiedCategory,
            inlineImages,
            downloadResult.BytesWritten,
            cancellationToken);
    }

    private HistoricalTrustResult ResolveHistoricalTrust(SlackChannelId channelId, string senderId)
    {
        var isExplicitChannel = _options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);
        var isExplicitUser = _options.AllowedUserIds.Contains(senderId, StringComparer.Ordinal);
        var isDirectMessage = channelId.Value.StartsWith("D", StringComparison.Ordinal);

        var audienceResult = AudienceResult.Resolve(
            channelId.Value, isDirectMessage,
            _options.ChannelAudiences,
            isExplicitUser: isExplicitUser,
            isExplicitChannel: isExplicitChannel);

        return new HistoricalTrustResult(
            audienceResult.Audience,
            isExplicitUser ? PrincipalClassification.TrustedInternal : PrincipalClassification.UntrustedExternal,
            audienceResult.Error);
    }

    private readonly record struct HistoricalTrustResult(
        TrustAudience Audience,
        PrincipalClassification Principal,
        string? Error);

    private static TextContent BuildHistoricalAttachmentRejected(string detail)
        => HistoricalAttachmentIngress.BuildRejected(detail);

    private static string BuildHistoricalAttachmentSourceKey(SlackNet.File file, string downloadUrl)
        => !string.IsNullOrWhiteSpace(file.Id)
            ? $"slack:{file.Id}"
            : $"slack-url:{downloadUrl}";
}
