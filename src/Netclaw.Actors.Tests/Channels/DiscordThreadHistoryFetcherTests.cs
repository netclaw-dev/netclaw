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
                    SenderId: "user-1",
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
                    SenderId: "user-2",
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
                    SenderId: "user-3",
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

    private static DiscordThreadHistoryFetcher CreateFetcher(
        DiscordThreadHistoryFetcher.MessageFetcher? messageFetcher = null,
        HttpMessageHandler? handler = null,
        IContentScanner? scanner = null,
        ToolAudienceProfiles? profiles = null,
        ModelCapabilities? modelCapabilities = null,
        DiscordChannelOptions? options = null)
    {
        return new DiscordThreadHistoryFetcher(
            messageFetcher ?? ((_, _) => Task.FromResult<IReadOnlyList<DiscordThreadHistoryFetcher.HistoricalMessage>>([])),
            options ?? new DiscordChannelOptions(),
            new HttpClient(handler ?? new FakeHttpHandler()),
            scanner ?? new NullContentScanner(),
            profiles ?? ToolAudienceProfileDefaults.CreateProfiles(),
            modelCapabilities ?? TestDiscordGatewayDeps.DefaultVisionCapableModel,
            new NetclawPaths(Path.GetTempPath()),
            NullLogger<DiscordThreadHistoryFetcher>.Instance);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
