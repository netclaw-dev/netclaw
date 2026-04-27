using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using SlackNet;
using SlackNet.WebApi;
using IOFile = System.IO.File;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Fetches prior messages from a Slack thread via <c>conversations.replies</c>
/// and returns them as <see cref="ChannelInput"/> items in chronological order.
/// </summary>
public sealed class SlackThreadHistoryFetcher : IThreadHistoryFetcher
{
    private const int PageSize = 200;
    private static readonly TimeSpan FileDownloadTimeout = TimeSpan.FromSeconds(10);

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
    private readonly ILogger<SlackThreadHistoryFetcher> _logger;

    public SlackThreadHistoryFetcher(
        RepliesFetcher repliesFetcher,
        SlackChannelOptions options,
        HttpClient httpClient,
        IContentScanner contentScanner,
        NetclawPaths paths,
        ILogger<SlackThreadHistoryFetcher> logger)
    {
        _repliesFetcher = repliesFetcher;
        _options = options;
        _httpClient = httpClient;
        _contentScanner = contentScanner;
        _paths = paths;
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
        ILogger<SlackThreadHistoryFetcher> logger)
        : this(
            (channelId, threadTs, limit, cursor, ct) =>
                conversationsApi.Replies(channelId.Value, threadTs.Value, limit: limit, cursor: cursor, cancellationToken: ct),
            options, httpClient, contentScanner, paths, logger)
    {
    }

    public async Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        // SessionId format: {channelId}/{threadTs}
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
        var results = new List<ChannelInput>();
        string? cursor = null;
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(sessionId, _paths.SessionsDirectory);

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
                // Include only human-authored thread messages.
                if (!string.IsNullOrWhiteSpace(message.BotId))
                    continue;

                if (string.IsNullOrWhiteSpace(message.User))
                    continue;

                var input = await ConvertMessageAsync(message, channelId, inboxDir, cancellationToken);
                if (input is not null)
                    results.Add(input);
            }

            cursor = response.ResponseMetadata?.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        _logger.LogInformation("Fetched {Count} thread history messages for {ChannelId}/{ThreadTs}", results.Count, channelId, threadTs);
        return results;
    }

    private async Task<ChannelInput?> ConvertMessageAsync(
        SlackNet.Events.MessageEvent message,
        SlackChannelId channelId,
        string inboxDir,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();

        if (!string.IsNullOrEmpty(message.Text))
            contents.Add(new TextContent(message.Text));

        if (message.Files is { Count: > 0 })
        {
            var downloadableFiles = message.Files
                .Where(f => f.Mimetype is not null
                    && !string.IsNullOrWhiteSpace(f.UrlPrivateDownload ?? f.UrlPrivate));

            var downloadTasks = downloadableFiles.Select(
                file => DownloadAndScanFileAsync(file, inboxDir, cancellationToken));
            var results = await Task.WhenAll(downloadTasks);

            foreach (var result in results)
            {
                if (result is not null)
                    contents.Add(result);
            }
        }

        if (contents.Count == 0)
            return null;

        var receivedAt = new SlackEventTs(message.Ts ?? string.Empty).ToDateTimeOffset() ?? default;

        return new ChannelInput
        {
            SenderId = message.User,
            ChannelId = channelId.Value,
            MessageId = $"{channelId.Value}:{message.Ts ?? string.Empty}",
            Contents = contents,
            ReceivedAt = receivedAt
        };
    }

    private async Task<DataContent?> DownloadAndScanFileAsync(
        SlackNet.File file, string inboxDir, CancellationToken cancellationToken)
    {
        var downloadUrl = file.UrlPrivateDownload ?? file.UrlPrivate!;
        var filename = file.Name ?? "attachment";
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(FileDownloadTimeout);

            var downloadResult = await SlackFileDownloader.DownloadToFileAsync(
                _httpClient, downloadUrl, _options.BotToken,
                inboxDir, ChannelAttachmentPolicy.DefaultMaxFileBytes, downloadCts.Token);

            if (downloadResult.BytesWritten == 0)
            {
                TryDeleteTemp(downloadResult.FilePath);
                return null;
            }

            var scanResult = await _contentScanner.ScanFileAsync(
                downloadResult.FilePath, filename, file.Mimetype!, cancellationToken);

            if (!scanResult.IsAllowed && scanResult.Error != ContentScanError.ScanFailure)
            {
                _logger.LogWarning("Content scan rejected backfill file {Name}: {Message}",
                    file.Name, scanResult.Message ?? scanResult.Error?.ToString());
                TryDeleteTemp(downloadResult.FilePath);
                return null;
            }

            var inboxPath = InboxWriter.SanitizeReserveAndMove(inboxDir, filename, downloadResult.FilePath);
            var bytes = await IOFile.ReadAllBytesAsync(inboxPath, cancellationToken);
            return new DataContent(bytes, file.Mimetype!);
        }
        catch (AttachmentTooLargeException)
        {
            _logger.LogWarning("Backfill file {Name} exceeded size limit, skipping", file.Name);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out downloading backfill file {Name}, skipping", file.Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download backfill file {Name}, skipping", file.Name);
            return null;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (IOFile.Exists(tempPath))
                IOFile.Delete(tempPath);
        }
        catch (IOException)
        {
            // best-effort cleanup during backfill; file will be orphaned but harmless
        }
    }
}
