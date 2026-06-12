// -----------------------------------------------------------------------
// <copyright file="MattermostThreadHistoryFetcherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Transport;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostThreadHistoryFetcherTests
{
    private const string ServerUrl = "https://mattermost.example.com";
    private const string BotUserId = "bot-user-id";

    [Fact]
    public async Task Includes_bot_authored_root_for_proactive_post_bootstrap()
    {
        // Mattermost session ID is {channelId}/{rootPostId}.
        // When the root post's MessageId matches the rootPostId from the
        // session, the bot-authored root MUST be included (proactive post).
        var fetcher = CreateFetcher(
            messageFetcher: (_, _) => Task.FromResult<IReadOnlyList<MattermostThreadHistoryFetcher.HistoricalMessage>>(
            [
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "root-post-001",
                    SenderId: new SenderId(BotUserId),
                    IsBot: true,
                    Text: "proactive post (root)",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "reply-post-002",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "human reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: [])
            ]));

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/root-post-001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);

        var rootEntry = Assert.Single(result, r => r.Contents.OfType<TextContent>()
            .Any(t => t.Text == "proactive post (root)"));
        Assert.Equal(BotUserId, rootEntry.SenderId.Value);

        var humanEntry = Assert.Single(result, r => r.Contents.OfType<TextContent>()
            .Any(t => t.Text == "human reply"));
        Assert.Equal("user-1", humanEntry.SenderId.Value);
    }

    [Fact]
    public async Task Excludes_bot_authored_replies_below_thread_root()
    {
        // Bot entries below the root were produced by one of our sessions
        // and are already in transcript. Re-adopting them from history
        // would surface our own outputs as third-party context.
        var fetcher = CreateFetcher(
            messageFetcher: (_, _) => Task.FromResult<IReadOnlyList<MattermostThreadHistoryFetcher.HistoricalMessage>>(
            [
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "root-post-001",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "human-started root",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "reply-post-002",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "human reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "reply-post-003",
                    SenderId: new SenderId("bot-other"),
                    IsBot: true,
                    Text: "third-party bot reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "reply-post-004",
                    SenderId: new SenderId(BotUserId),
                    IsBot: true,
                    Text: "our own prior bot reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: [])
            ]));

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/root-post-001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "human-started root"));
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "human reply"));

        Assert.DoesNotContain(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "third-party bot reply"));
        Assert.DoesNotContain(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "our own prior bot reply"));
    }

    [Fact]
    public async Task Excludes_bot_below_root_even_when_root_is_also_bot()
    {
        // Proactive root is bot AND there's a later bot turn (already in
        // transcript). Only the root and the user reply survive backfill.
        var fetcher = CreateFetcher(
            messageFetcher: (_, _) => Task.FromResult<IReadOnlyList<MattermostThreadHistoryFetcher.HistoricalMessage>>(
            [
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "root-post-001",
                    SenderId: new SenderId(BotUserId),
                    IsBot: true,
                    Text: "proactive root",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "reply-post-002",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "user reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "reply-post-003",
                    SenderId: new SenderId(BotUserId),
                    IsBot: true,
                    Text: "agent's reply turn (in transcript)",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: [])
            ]));

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/root-post-001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "proactive root"));
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "user reply"));
        Assert.DoesNotContain(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "agent's reply turn (in transcript)"));
    }

    [Fact]
    public async Task Attachment_only_historical_message_is_preserved_and_inlined()
    {
        // Message with empty text and a single image attachment.
        // FileDownloader writes PNG magic bytes; the result should contain
        // a DataContent with the image MIME type.
        MattermostThreadHistoryFetcher.FileDownloader fileDownloader = async (fileId, stagingDir, maxBytes, ct) =>
        {
            var path = Path.Combine(stagingDir, $"{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ct); // PNG magic bytes
            return (path, 4L);
        };

        var fetcher = CreateFetcher(
            messageFetcher: (_, _) => Task.FromResult<IReadOnlyList<MattermostThreadHistoryFetcher.HistoricalMessage>>(
            [
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "msg-1001",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: string.Empty,
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new MattermostFileReference(
                            "screenshot.png",
                            "image/png",
                            4,
                            $"{ServerUrl}/api/v4/files/file123")
                    ])
            ]),
            fileDownloader: fileDownloader);

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/msg-1001"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Contains(item.Contents, c => c is DataContent d && d.MediaType == "image/png");
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("[attachment]", StringComparison.Ordinal)
              && t.Text.Contains("screenshot.png", StringComparison.Ordinal)
              && t.Text.Contains("inlined=\"true\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Historical_scan_failure_is_rejected_fail_closed()
    {
        MattermostThreadHistoryFetcher.FileDownloader fileDownloader = async (fileId, stagingDir, maxBytes, ct) =>
        {
            var path = Path.Combine(stagingDir, $"{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ct);
            return (path, 4L);
        };

        var fetcher = CreateFetcher(
            messageFetcher: (_, _) => Task.FromResult<IReadOnlyList<MattermostThreadHistoryFetcher.HistoricalMessage>>(
            [
                new MattermostThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "msg-1003",
                    SenderId: new SenderId("user-3"),
                    IsBot: false,
                    Text: "please inspect",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new MattermostFileReference(
                            "drawing.png",
                            "image/png",
                            4,
                            $"{ServerUrl}/api/v4/files/file456")
                    ])
            ]),
            fileDownloader: fileDownloader,
            scanner: new FailingContentScanner());

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/msg-1003"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.DoesNotContain(item.Contents, c => c is DataContent);
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("attachment rejected", StringComparison.OrdinalIgnoreCase)
              && t.Text.Contains("content scanning", StringComparison.OrdinalIgnoreCase));
    }

    private static MattermostThreadHistoryFetcher CreateFetcher(
        MattermostThreadHistoryFetcher.MessageFetcher? messageFetcher = null,
        MattermostThreadHistoryFetcher.FileDownloader? fileDownloader = null,
        IContentScanner? scanner = null,
        ToolAudienceProfiles? profiles = null,
        ModelCapabilities? modelCapabilities = null,
        MattermostChannelOptions? options = null,
        NetclawPaths? paths = null)
    {
        return new MattermostThreadHistoryFetcher(
            messageFetcher ?? ((_, _) => Task.FromResult<IReadOnlyList<MattermostThreadHistoryFetcher.HistoricalMessage>>([])),
            fileDownloader ?? ((_, _, _, _) => Task.FromResult<(string FilePath, long BytesWritten)?>(null)),
            scanner ?? new NullContentScanner(),
            options ?? new MattermostChannelOptions(),
            ServerUrl,
            BotUserId,
            profiles ?? TestMattermostGatewayDeps.DefaultAudienceProfiles,
            modelCapabilities ?? TestMattermostGatewayDeps.DefaultVisionCapableModel,
            paths ?? TestMattermostGatewayDeps.NewTestPaths(),
            NullLogger<MattermostThreadHistoryFetcher>.Instance);
    }

    private sealed class FailingContentScanner : IContentScanner
    {
        public Task<ContentScanResult> ScanAsync(
            ReadOnlyMemory<byte> content,
            string filename,
            string declaredMimeType,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ContentScanResult.Rejected(
                ContentScanError.ScanFailure,
                "Content scan failed: scanner unavailable"));
        }
    }
}
