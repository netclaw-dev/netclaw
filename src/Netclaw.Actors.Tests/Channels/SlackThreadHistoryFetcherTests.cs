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

    private SlackThreadHistoryFetcher CreateFetcher() => new(
        _replies.FetchAsync,
        _options,
        new HttpClient(new FakeHttpHandler()),
        new NullContentScanner(),
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
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
            string channelId, string threadTs, int limit, string? cursor, CancellationToken ct)
        {
            if (ThrowOnFetch is not null)
                throw ThrowOnFetch;

            var key = $"{channelId}:{threadTs}:{cursor ?? ""}";
            return _responses.TryGetValue(key, out var response)
                ? Task.FromResult(response)
                : Task.FromResult(new ConversationMessagesResponse());
        }
    }
}
