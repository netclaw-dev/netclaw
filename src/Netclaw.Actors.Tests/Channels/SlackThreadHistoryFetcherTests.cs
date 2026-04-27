using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackThreadHistoryFetcherTests
{
    private readonly FakeReplies _replies = new();

    private readonly SlackChannelOptions _options = new()
    {
        BotToken = new SensitiveString("xoxb-test")
    };

    private SlackThreadHistoryFetcher CreateFetcher(
        HttpMessageHandler? handler = null,
        IContentScanner? scanner = null,
        ToolAudienceProfiles? profiles = null,
        ModelCapabilities? modelCapabilities = null,
        NetclawPaths? paths = null) => new(
            _replies.FetchAsync,
            _options,
            new HttpClient(handler ?? new FakeHttpHandler()),
            scanner ?? new NullContentScanner(),
            paths ?? new NetclawPaths(Path.GetTempPath()),
            profiles ?? ToolAudienceProfileDefaults.CreateProfiles(),
            modelCapabilities ?? TestSlackGatewayDeps.DefaultVisionCapableModel,
            NullLogger<SlackThreadHistoryFetcher>.Instance);

    [Fact]
    public async Task Fetches_text_messages_from_thread()
    {
        _replies.Set("C1", "1000.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent { Ts = "1000.0", User = "U1", Text = "thread root" },
                new MessageEvent { Ts = "1000.1", User = "U2", Text = "reply one" },
                new MessageEvent { Ts = "1000.2", User = "U3", Text = "reply two" }
            ]
        });

        var result = await CreateFetcher().FetchThreadHistoryAsync(new SessionId("C1/1000.0"), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.MessageId == "C1:1000.0");
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "reply one"));
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "reply two"));
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "thread root"));
    }

    [Fact]
    public async Task Filters_out_bot_messages()
    {
        _replies.Set("C1", "1000.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent { Ts = "1000.0", User = "U1", Text = "root" },
                new MessageEvent { Ts = "1000.1", User = "U2", Text = "human reply" },
                new MessageEvent { Ts = "1000.2", BotId = "B_BOT", Text = "bot reply" },
                new MessageEvent { Ts = "1000.3", BotId = "B_NETCLAW", Text = "netclaw reply" }
            ]
        });

        var result = await CreateFetcher().FetchThreadHistoryAsync(new SessionId("C1/1000.0"), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "root"));
        Assert.Contains(result, r => r.Contents.OfType<TextContent>().Any(t => t.Text == "human reply"));
    }

    [Fact]
    public async Task Returns_empty_list_on_api_error()
    {
        _replies.ThrowOnFetch = new SlackException(new ErrorResponse { Error = "channel_not_found" });

        var result = await CreateFetcher().FetchThreadHistoryAsync(new SessionId("C1/1000.0"), TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Returns_empty_list_for_invalid_session_id()
    {
        var result = await CreateFetcher().FetchThreadHistoryAsync(new SessionId("no-slash"), TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Historical_non_image_files_are_downloaded_and_announced_path_only()
    {
        _replies.Set("D1", "2000.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent { Ts = "2000.0", User = "U1", Text = "root" },
                new MessageEvent
                {
                    Ts = "2000.1",
                    User = "U2",
                    Text = "check this report",
                    Files =
                    [
                        new SlackNet.File
                        {
                            Id = "F_PDF",
                            Name = "report.pdf",
                            Mimetype = "application/pdf",
                            Size = 3,
                            UrlPrivateDownload = "https://files.slack.com/fake/report.pdf"
                        }
                    ]
                }
            ]
        });

        var result = await CreateFetcher().FetchThreadHistoryAsync(
            new SessionId("D1/2000.0"), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        var messageWithPdf = result.First(r => r.MessageId == "D1:2000.1");
        Assert.DoesNotContain(messageWithPdf.Contents, c => c is DataContent);
        var attachmentText = Assert.Single(
            messageWithPdf.Contents.OfType<TextContent>(),
            t => t.Text.Contains("[attachment]", StringComparison.Ordinal));
        Assert.Contains("report.pdf", attachmentText.Text, StringComparison.Ordinal);
        Assert.Contains("inlined=\"false\"", attachmentText.Text, StringComparison.Ordinal);
        Assert.Contains("path=\"inbox/report", attachmentText.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Historical_scan_failure_is_rejected_fail_closed()
    {
        _replies.Set("C1", "2100.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent
                {
                    Ts = "2100.1",
                    User = "U2",
                    Text = "look at this",
                    Files =
                    [
                        new SlackNet.File
                        {
                            Id = "F_IMG",
                            Name = "photo.png",
                            Mimetype = "image/png",
                            Size = 3,
                            UrlPrivateDownload = "https://files.slack.com/fake/photo.png"
                        }
                    ]
                }
            ]
        });

        var result = await CreateFetcher(scanner: new FailingContentScanner()).FetchThreadHistoryAsync(
            new SessionId("C1/2100.0"), TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.DoesNotContain(item.Contents, c => c is DataContent);
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("attachment rejected", StringComparison.OrdinalIgnoreCase)
              && t.Text.Contains("content scanning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Historical_inline_image_includes_attachment_line_and_data_content()
    {
        _replies.Set("D2", "2150.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent
                {
                    Ts = "2150.1",
                    User = "U2",
                    Files =
                    [
                        new SlackNet.File
                        {
                            Id = "F_IMG",
                            Name = "photo.png",
                            Mimetype = "image/png",
                            Size = 3,
                            UrlPrivateDownload = "https://files.slack.com/fake/photo.png"
                        }
                    ]
                }
            ]
        });

        var result = await CreateFetcher().FetchThreadHistoryAsync(
            new SessionId("D2/2150.0"), TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Contains(item.Contents, c => c is DataContent d && d.MediaType == "image/png");
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("[attachment]", StringComparison.Ordinal)
              && t.Text.Contains("photo.png", StringComparison.Ordinal)
              && t.Text.Contains("inlined=\"true\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Historical_attachment_size_limit_uses_resolved_audience_policy()
    {
        var handler = new FakeHttpHandler();
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        profiles.Public.ChannelAttachments = new ChannelAttachmentPolicy
        {
            AllowedCategories = [AttachmentCategory.Pdf],
            MaxFileBytes = 1,
            MaxFilesPerMessage = ChannelAttachmentPolicy.DefaultMaxFilesPerMessage
        };

        _replies.Set("C1", "2200.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent
                {
                    Ts = "2200.1",
                    User = "U2",
                    Files =
                    [
                        new SlackNet.File
                        {
                            Id = "F_PDF",
                            Name = "report.pdf",
                            Mimetype = "application/pdf",
                            Size = 3,
                            UrlPrivateDownload = "https://files.slack.com/fake/report.pdf"
                        }
                    ]
                }
            ]
        });

        var result = await CreateFetcher(handler: handler, profiles: profiles).FetchThreadHistoryAsync(
            new SessionId("C1/2200.0"), TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(item.Contents.OfType<TextContent>(),
            t => t.Text.Contains("attachment rejected", StringComparison.OrdinalIgnoreCase)
              && t.Text.Contains("per-file limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Historical_attachment_reuse_skips_repeat_downloads()
    {
        var sessionsRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var handler = new FakeHttpHandler();

        _replies.Set("D3", "2300.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent
                {
                    Ts = "2300.1",
                    User = "U2",
                    Files =
                    [
                        new SlackNet.File
                        {
                            Id = "F_REUSE",
                            Name = "reuse.png",
                            Mimetype = "image/png",
                            Size = 3,
                            UrlPrivateDownload = "https://files.slack.com/fake/reuse.png"
                        }
                    ]
                }
            ]
        });

        var fetcher = CreateFetcher(handler: handler, paths: new NetclawPaths(sessionsRoot));
        var sessionId = new SessionId("D3/2300.0");

        var first = await fetcher.FetchThreadHistoryAsync(sessionId, TestContext.Current.CancellationToken);
        var second = await fetcher.FetchThreadHistoryAsync(sessionId, TestContext.Current.CancellationToken);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Paginates_through_all_pages()
    {
        _replies.Set("C1", "1000.0", null, new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent { Ts = "1000.0", User = "U1", Text = "root" },
                new MessageEvent { Ts = "1000.1", User = "U2", Text = "page 1" }
            ],
            ResponseMetadata = new ResponseMetadata { NextCursor = "cursor_page2" }
        });

        _replies.Set("C1", "1000.0", "cursor_page2", new ConversationMessagesResponse
        {
            Messages =
            [
                new MessageEvent { Ts = "1000.2", User = "U3", Text = "page 2" }
            ]
        });

        var result = await CreateFetcher().FetchThreadHistoryAsync(new SessionId("C1/1000.0"), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
    }

    // --- Fakes ---

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
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

    private sealed class FakeReplies
    {
        private readonly Dictionary<string, ConversationMessagesResponse> _responses = new();
        public SlackException? ThrowOnFetch { get; set; }

        public void Set(string channel, string threadTs, string? cursor, ConversationMessagesResponse response)
        {
            var key = $"{channel}:{threadTs}:{cursor ?? ""}";
            _responses[key] = response;
        }

        public Task<ConversationMessagesResponse> FetchAsync(
            SlackChannelId channelId, SlackThreadTs threadTs, int limit, string? cursor, CancellationToken ct)
        {
            if (ThrowOnFetch is not null)
                throw ThrowOnFetch;

            var key = $"{channelId.Value}:{threadTs.Value}:{cursor ?? ""}";
            return _responses.TryGetValue(key, out var response)
                ? Task.FromResult(response)
                : Task.FromResult(new ConversationMessagesResponse());
        }
    }
}
