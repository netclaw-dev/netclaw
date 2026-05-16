// -----------------------------------------------------------------------
// <copyright file="DiscordThreadHistoryFetcherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordThreadHistoryFetcherTests
{
    [Fact]
    public async Task Attachment_only_historical_message_is_preserved_and_inlined()
    {
        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "1001",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: string.Empty,
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new DiscordFileReference(
                            "screenshot.png",
                            "image/png",
                            3,
                            "https://cdn.discordapp.com/attachments/1/2/screenshot.png")
                    ])
            ]));

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/100000000000000001"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Contains(item.Contents, c => c is DataContent d && d.MediaType == "image/png");
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("[attachment]", StringComparison.Ordinal)
              && t.Text.Contains("screenshot.png", StringComparison.Ordinal)
              && t.Text.Contains("inlined=\"true\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Historical_non_image_file_is_announced_path_only()
    {
        var options = new DiscordChannelOptions
        {
            AllowedChannelIds = ["ch-team"]
        };

        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "1002",
                    SenderId: new SenderId("user-2"),
                    IsBot: false,
                    Text: "see attached",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new DiscordFileReference(
                            "report.pdf",
                            "application/pdf",
                            3,
                            "https://cdn.discordapp.com/attachments/1/2/report.pdf")
                    ])
            ]),
            options: options);

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-team/100000000000000002"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.DoesNotContain(item.Contents, c => c is DataContent);
        Assert.Contains(item.Contents.OfType<TextContent>(), t => t.Text.Contains("see attached", StringComparison.Ordinal));
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("[attachment]", StringComparison.Ordinal)
              && t.Text.Contains("report.pdf", StringComparison.Ordinal)
              && t.Text.Contains("path=\"inbox/report", StringComparison.Ordinal)
              && t.Text.Contains("inlined=\"false\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Historical_scan_failure_is_rejected_fail_closed()
    {
        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "1003",
                    SenderId: new SenderId("user-3"),
                    IsBot: false,
                    Text: "please inspect",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new DiscordFileReference(
                            "drawing.png",
                            "image/png",
                            3,
                            "https://cdn.discordapp.com/attachments/1/2/drawing.png")
                    ])
            ]),
            scanner: new FailingContentScanner());

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/100000000000000003"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.DoesNotContain(item.Contents, c => c is DataContent);
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("attachment rejected", StringComparison.OrdinalIgnoreCase)
              && t.Text.Contains("content scanning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Historical_dm_attachment_uses_dm_audience_policy()
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        profiles.Team.ChannelAttachments = new ChannelAttachmentPolicy
        {
            AllowedCategories = [AttachmentCategory.Pdf],
            MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
            MaxFilesPerMessage = ChannelAttachmentPolicy.DefaultMaxFilesPerMessage
        };
        profiles.Public.ChannelAttachments = ChannelAttachmentPolicy.Empty;

        var options = new DiscordChannelOptions
        {
            AllowDirectMessages = true,
            ChannelAudiences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dm"] = "team"
            }
        };

        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "1004",
                    SenderId: new SenderId("user-4"),
                    IsBot: false,
                    Text: string.Empty,
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new DiscordFileReference(
                            "report.pdf",
                            "application/pdf",
                            3,
                            "https://cdn.discordapp.com/attachments/1/2/report.pdf")
                    ])
            ]),
            profiles: profiles,
            options: options);

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("100000000000000004/100000000000000004"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("[attachment]", StringComparison.Ordinal)
              && t.Text.Contains("report.pdf", StringComparison.Ordinal)
              && t.Text.Contains("inlined=\"false\"", StringComparison.Ordinal));
        Assert.DoesNotContain(item.Contents, c => c is DataContent);
    }

    [Fact]
    public async Task Unknown_channel_is_not_treated_as_dm_when_dm_enabled()
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        profiles.Team.ChannelAttachments = new ChannelAttachmentPolicy
        {
            AllowedCategories = [AttachmentCategory.Pdf],
            MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
            MaxFilesPerMessage = ChannelAttachmentPolicy.DefaultMaxFilesPerMessage
        };
        profiles.Public.ChannelAttachments = ChannelAttachmentPolicy.Empty;

        var options = new DiscordChannelOptions
        {
            AllowDirectMessages = true,
            ChannelAudiences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dm"] = "team"
            }
        };

        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "1006",
                    SenderId: new SenderId("user-6"),
                    IsBot: false,
                    Text: string.Empty,
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new DiscordFileReference(
                            "report.pdf",
                            "application/pdf",
                            3,
                            "https://cdn.discordapp.com/attachments/1/2/report.pdf")
                    ])
            ]),
            profiles: profiles,
            options: options);

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("100000000000000006/100000000000000007"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("attachment rejected", StringComparison.OrdinalIgnoreCase)
              && t.Text.Contains("exceed the 0 per-message limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Historical_attachment_reuse_skips_repeat_downloads()
    {
        var sessionsRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var handler = new FakeHttpHandler();

        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "1005",
                    SenderId: new SenderId("user-5"),
                    IsBot: false,
                    Text: string.Empty,
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments:
                    [
                        new DiscordFileReference(
                            "repeat.png",
                            "image/png",
                            3,
                            "https://cdn.discordapp.com/attachments/1/2/repeat.png")
                    ])
            ]),
            handler: handler,
            paths: new NetclawPaths(sessionsRoot));

        var sessionId = new SessionId("ch-public/100000000000000005");
        var first = await fetcher.FetchThreadHistoryAsync(sessionId, TestContext.Current.CancellationToken);
        var second = await fetcher.FetchThreadHistoryAsync(sessionId, TestContext.Current.CancellationToken);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Includes_bot_authored_root_for_proactive_post_bootstrap()
    {
        // Discord conventions: thread channel id equals the root message id.
        // Session id is `{channelId}/{threadChannelId}`; the entry whose
        // MessageId matches the threadChannelId IS the root.
        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000002001",
                    SenderId: new SenderId("bot-1"),
                    IsBot: true,
                    Text: "proactive post (root)",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000002002",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "human reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: [])
            ]));

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/100000000000002001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);

        var rootEntry = Assert.Single(result, r => r.Contents.OfType<TextContent>()
            .Any(t => t.Text == "proactive post (root)"));
        Assert.Equal("bot-1", rootEntry.SenderId.Value);

        var humanEntry = Assert.Single(result, r => r.Contents.OfType<TextContent>()
            .Any(t => t.Text == "human reply"));
        Assert.Equal("user-1", humanEntry.SenderId.Value);
    }

    [Fact]
    public async Task Excludes_bot_authored_replies_below_thread_root()
    {
        // Regression test for issue #955: bot entries below the root were
        // produced by one of our sessions and are already in transcript.
        // Re-adopting them from history surfaces our own outputs as
        // third-party context.
        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000003001",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "human-started root",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000003002",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "human reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000003003",
                    SenderId: new SenderId("bot-other"),
                    IsBot: true,
                    Text: "third-party bot reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000003004",
                    SenderId: new SenderId("bot-netclaw"),
                    IsBot: true,
                    Text: "our own prior bot reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: [])
            ]));

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/100000000000003001"),
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
        // transcript). Only the root survives backfill.
        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000004001",
                    SenderId: new SenderId("bot-netclaw"),
                    IsBot: true,
                    Text: "proactive root",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000004002",
                    SenderId: new SenderId("user-1"),
                    IsBot: false,
                    Text: "user reply",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: []),
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000004003",
                    SenderId: new SenderId("bot-netclaw"),
                    IsBot: true,
                    Text: "agent's reply turn (in transcript)",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: [])
            ]));

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("ch-public/100000000000004001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "proactive root"));
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "user reply"));
        Assert.DoesNotContain(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "agent's reply turn (in transcript)"));
    }

    [Fact]
    public async Task Hydrated_channel_input_carries_resolved_historical_audience()
    {
        // Locks in the invariant that the Slack twin regressed on: the fetcher
        // MUST propagate the resolved historical audience to the produced
        // ChannelInput so hydration-driven backfill doesn't silently fall back
        // to the channel pipeline's DefaultAudience.
        var options = new DiscordChannelOptions
        {
            AllowDirectMessages = true,
            ChannelAudiences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dm"] = "personal"
            }
        };

        var fetcher = CreateFetcher(
            (_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>(
            [
                new DiscordThreadHistoryFetcher.HistoricalMessage(
                    MessageId: "100000000000000010",
                    SenderId: new SenderId("user-10"),
                    IsBot: false,
                    Text: "first DM message",
                    Timestamp: TimeProvider.System.GetUtcNow(),
                    Attachments: [])
            ]),
            options: options);

        var result = await fetcher.FetchThreadHistoryAsync(
            new SessionId("100000000000000010/100000000000000010"),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Equal(TrustAudience.Personal, item.Audience);
    }

    private static DiscordThreadHistoryFetcher CreateFetcher(
        DiscordThreadHistoryFetcher.MessageFetcher? messageFetcher = null,
        HttpMessageHandler? handler = null,
        IContentScanner? scanner = null,
        ToolAudienceProfiles? profiles = null,
        ModelCapabilities? modelCapabilities = null,
        DiscordChannelOptions? options = null,
        NetclawPaths? paths = null)
    {
        return new DiscordThreadHistoryFetcher(
            messageFetcher ?? ((_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>([])),
            options ?? new DiscordChannelOptions(),
            new HttpClient(handler ?? new FakeHttpHandler()),
            scanner ?? new NullContentScanner(),
            profiles ?? ToolAudienceProfileDefaults.CreateProfiles(),
            modelCapabilities ?? TestDiscordGatewayDeps.DefaultVisionCapableModel,
            paths ?? new NetclawPaths(Path.GetTempPath()),
            NullLogger<DiscordThreadHistoryFetcher>.Instance);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }
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
