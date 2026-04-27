using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Security;
using IOFile = System.IO.File;

namespace Netclaw.Channels.Discord.Transport;

public sealed class DiscordThreadHistoryFetcher : IThreadHistoryFetcher
{
    private const int MaxMessages = 200;
    private static readonly TimeSpan FileDownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContentScanTimeout = TimeSpan.FromSeconds(5);

    internal sealed record HistoricalMessage(
        string MessageId,
        string SenderId,
        bool IsBot,
        string Text,
        DateTimeOffset Timestamp,
        IReadOnlyList<DiscordFileReference> Attachments);

    internal delegate Task<IReadOnlyList<HistoricalMessage>> MessageFetcher(
        ulong threadChannelId,
        CancellationToken cancellationToken);

    private readonly MessageFetcher _messageFetcher;
    private readonly DiscordChannelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IContentScanner _contentScanner;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly NetclawPaths _paths;
    private readonly ILogger<DiscordThreadHistoryFetcher> _logger;

    public DiscordThreadHistoryFetcher(
        DiscordSocketClient client,
        DiscordChannelOptions options,
        HttpClient httpClient,
        IContentScanner contentScanner,
        ToolAudienceProfiles audienceProfiles,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths,
        ILogger<DiscordThreadHistoryFetcher> logger)
        : this(
            (threadChannelId, cancellationToken) => FetchRawMessagesAsync(client, threadChannelId, cancellationToken, logger),
            options,
            httpClient,
            contentScanner,
            audienceProfiles,
            modelCapabilities,
            paths,
            logger)
    {
    }

    internal DiscordThreadHistoryFetcher(
        MessageFetcher messageFetcher,
        DiscordChannelOptions options,
        HttpClient httpClient,
        IContentScanner contentScanner,
        ToolAudienceProfiles audienceProfiles,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths,
        ILogger<DiscordThreadHistoryFetcher> logger)
    {
        _messageFetcher = messageFetcher;
        _options = options;
        _httpClient = httpClient;
        _contentScanner = contentScanner;
        _audienceProfiles = audienceProfiles;
        _modelCapabilities = modelCapabilities;
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!DiscordGatewayActor.TryParseDiscordSessionId(sessionId, out var channelId, out var threadOrMessageId))
        {
            _logger.LogWarning("Cannot extract channel/thread from session ID {SessionId}", sessionId.Value);
            return [];
        }

        if (!ulong.TryParse(threadOrMessageId.Value, out var threadChannelId))
        {
            _logger.LogWarning("Thread portion of session ID is not a valid snowflake: {SessionId}", sessionId.Value);
            return [];
        }

        var audienceResult = ResolveHistoricalAudience(channelId, threadOrMessageId);
        if (audienceResult.Error is { } audienceError)
        {
            _logger.LogWarning(
                "Invalid Discord audience configuration while fetching history for {SessionId}: {Error}",
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
            var history = await _messageFetcher(threadChannelId, cancellationToken);
            var results = new List<ChannelInput>(history.Count);

            foreach (var message in history)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (message.IsBot)
                    continue;

                var input = await ConvertMessageAsync(
                    message,
                    channelId,
                    threadChannelId,
                    audience,
                    attachmentPolicy,
                    inlineImages,
                    inboxDir,
                    stagingDir,
                    cancellationToken);
                if (input is not null)
                    results.Add(input);
            }

            _logger.LogInformation("Fetched {Count} thread history messages for thread {ThreadId}", results.Count, threadChannelId);
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
        DiscordChannelId channelId,
        ulong threadChannelId,
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
                    "Skipping {Count} historical Discord attachments on thread {ThreadId}; limit is {Limit} for audience {Audience}",
                    message.Attachments.Count,
                    threadChannelId,
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
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                PayloadTaint = PayloadTaint.Public,
                SourceKind = "discord",
                SourceScope = threadChannelId.ToString()
            },
            Contents = contents,
            ReceivedAt = message.Timestamp
        };
    }

    private async Task<IReadOnlyList<AIContent>> DownloadAndProjectAttachmentAsync(
        string messageId,
        DiscordFileReference file,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDir,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var category = AttachmentCategories.FromMime(file.MimeType);
        var sourceKey = BuildHistoricalAttachmentSourceKey(messageId, file);

        if (!policy.Allows(category))
        {
            _logger.LogWarning(
                "Historical Discord attachment {Name} rejected: category {Category} not allowed for {Audience}",
                file.Name,
                category,
                audience);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment ({file.MimeType}) category not allowed in {audience}")];
        }

        if (file.Size > policy.MaxFileBytes)
        {
            _logger.LogWarning(
                "Historical Discord attachment {Name} rejected: size {Size} exceeds {Limit}",
                file.Name,
                file.Size,
                policy.MaxFileBytes);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" exceeds the {AttachmentIngressFormatting.FormatBytes(policy.MaxFileBytes)} per-file limit")];
        }

        if (HistoricalAttachmentInbox.TryGetExistingFile(inboxDir, file.Name, sourceKey, out var existingPath, out var existingSize))
            return await BuildAcceptedAttachmentContentsAsync(
                existingPath,
                file.Name,
                file.MimeType,
                category,
                inlineImages,
                existingSize,
                cancellationToken);

        if (!DiscordAttachmentUrlTrust.IsAllowedAttachmentDomain(file.Url))
        {
            _logger.LogWarning(
                "Historical Discord attachment {Name} rejected: untrusted domain {Url}",
                file.Name,
                file.Url);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" has an untrusted download URL")];
        }

        AttachmentDownloadResult downloadResult;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(FileDownloadTimeout);
            downloadResult = await StreamingAttachmentDownloader.DownloadToFileAsync(
                _httpClient,
                file.Url,
                configureRequest: null,
                stagingDir,
                policy.MaxFileBytes,
                downloadCts.Token,
                (ex, path) => _logger.LogError(ex, "Failed to clean up staged download file {Path}", path));
        }
        catch (AttachmentTooLargeException ex)
        {
            _logger.LogWarning(
                "Historical Discord attachment {Name} rejected during download: {Size} exceeds {Limit}",
                file.Name,
                ex.BytesReceived,
                ex.MaxBytes);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" exceeded the {AttachmentIngressFormatting.FormatBytes(ex.MaxBytes)} per-file limit during download")];
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out downloading historical Discord attachment {Name}", file.Name);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" timed out during download")];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed downloading historical Discord attachment {Name}", file.Name);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" could not be downloaded")];
        }

        if (downloadResult.BytesWritten == 0)
        {
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" downloaded as zero bytes")];
        }

        ContentScanResult scanResult;
        try
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scanCts.CancelAfter(ContentScanTimeout);
            scanResult = await _contentScanner.ScanFileAsync(
                downloadResult.FilePath,
                file.Name,
                file.MimeType,
                scanCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Historical Discord attachment scan threw for {Name}", file.Name);
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" could not be scanned")];
        }

        if (!scanResult.IsAllowed)
        {
            _logger.LogWarning(
                "Historical Discord attachment {Name} rejected by scanner: {Error} {Message}",
                file.Name,
                scanResult.Error?.ToString(),
                scanResult.Message ?? string.Empty);
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" was rejected by content scanning: {AttachmentIngressFormatting.EscapeQuoted(scanResult.Message ?? scanResult.Error?.ToString() ?? "unknown error")}")];
        }

        string inboxPath;
        try
        {
            inboxPath = HistoricalAttachmentInbox.PromoteOrReuse(
                inboxDir,
                file.Name,
                sourceKey,
                downloadResult.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to promote historical Discord attachment {Name} into inbox", file.Name);
            AttachmentStagingCleanup.TryDelete(downloadResult.FilePath, _logger);
            return [BuildHistoricalAttachmentRejected(
                $"historical attachment \"{AttachmentIngressFormatting.EscapeQuoted(file.Name)}\" could not be saved to the session inbox")];
        }

        return await BuildAcceptedAttachmentContentsAsync(
            inboxPath,
            file.Name,
            file.MimeType,
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

    private AudienceResult ResolveHistoricalAudience(
        DiscordChannelId channelId,
        DiscordThreadOrMessageId threadOrMessageId)
    {
        var isExplicitChannel = _options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);
        var isDirectMessage = string.Equals(channelId.Value, threadOrMessageId.Value, StringComparison.Ordinal);
        var syntheticMessage = new DiscordGatewayMessage(
            EventId: new DiscordEventId($"history-{channelId.Value}"),
            ChannelId: channelId,
            ReplyChannelId: new DiscordReplyChannelId(threadOrMessageId.Value),
            MessageId: new DiscordMessageId($"history-{channelId.Value}"),
            ThreadOrMessageId: threadOrMessageId,
            RootMessageId: null,
            SenderId: new DiscordUserId("history"),
            IsBotMessage: false,
            IsDirectMessage: isDirectMessage,
            ContainsBotMention: false,
            Text: string.Empty,
            ReceivedAt: TimeProvider.System.GetUtcNow());

        return DiscordAclPolicy.ResolveAudience(
            syntheticMessage,
            _options,
            isExplicitUser: false,
            isExplicitChannel: isExplicitChannel);
    }

    private static async Task<IReadOnlyList<HistoricalMessage>> FetchRawMessagesAsync(
        DiscordSocketClient client,
        ulong threadChannelId,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        var channel = client.GetChannel(threadChannelId) as IMessageChannel;
        if (channel is null)
        {
            logger.LogWarning("Discord channel {ChannelId} not found or is not a message channel", threadChannelId);
            return [];
        }

        var results = new List<HistoricalMessage>();

        if (channel is SocketThreadChannel threadChannel)
        {
            var parentChannel = threadChannel.ParentChannel as IMessageChannel;
            if (parentChannel is not null)
            {
                try
                {
                    var rootMessage = await parentChannel.GetMessageAsync(
                        threadChannelId,
                        options: new RequestOptions { CancelToken = cancellationToken });

                    if (rootMessage is not null && !rootMessage.Author.IsBot && HasUsableContent(rootMessage))
                        results.Add(ToHistoricalMessage(rootMessage));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to fetch root message for thread {ThreadId}", threadChannelId);
                }
            }
        }

        var messages = await channel
            .GetMessagesAsync(MaxMessages, options: new RequestOptions { CancelToken = cancellationToken })
            .FlattenAsync();

        foreach (var message in messages.OrderBy(m => m.Timestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (message.Author.IsBot)
                continue;

            if (!HasUsableContent(message))
                continue;

            results.Add(ToHistoricalMessage(message));
        }

        return results;
    }

    private static HistoricalMessage ToHistoricalMessage(IMessage message)
        => new(
            MessageId: message.Id.ToString(),
            SenderId: message.Author.Id.ToString(),
            IsBot: message.Author.IsBot,
            Text: message.Content ?? string.Empty,
            Timestamp: message.Timestamp,
            Attachments: message.Attachments
                .Select(a => new DiscordFileReference(
                    a.Filename,
                    a.ContentType ?? "application/octet-stream",
                    a.Size,
                    a.Url))
                .ToArray());

    private static bool HasUsableContent(IMessage message)
        => !string.IsNullOrWhiteSpace(message.Content) || message.Attachments.Count > 0;

    private static TextContent BuildHistoricalAttachmentRejected(string detail)
        => new($"[attachment rejected: {detail}]");

    private static string BuildHistoricalAttachmentSourceKey(string messageId, DiscordFileReference file)
        => $"discord:{messageId}:{file.Url}";
}
