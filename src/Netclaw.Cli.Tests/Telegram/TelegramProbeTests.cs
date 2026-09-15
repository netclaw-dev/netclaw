// -----------------------------------------------------------------------
// <copyright file="TelegramProbeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Cli.Telegram;
using Xunit;

namespace Netclaw.Cli.Tests.Telegram;

public sealed class TelegramProbeTests
{
    [Fact]
    public async Task Probe_returns_bot_username_from_getMe()
    {
        var probe = CreateProbe(_ => Json(HttpStatusCode.OK, """{"ok":true,"result":{"username":"netclaw_bot"}}"""));
        var result = await probe.ProbeAsync("token", TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal("netclaw_bot", result.BotUsername);
    }

    [Fact]
    public async Task Probe_reports_an_invalid_token()
    {
        var probe = CreateProbe(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        var result = await probe.ProbeAsync("bad-token", TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("Bot token is invalid.", result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveChatIds_rejects_non_numeric_ids_and_returns_canonical_ids()
    {
        var probe = CreateProbe(request => Json(HttpStatusCode.OK,
            request.RequestUri!.Query.Contains("-5364308250", StringComparison.Ordinal)
                ? """{"ok":true,"result":{"id":-5364308250,"type":"supergroup","title":"Netclaw group"}}"""
                : "{}"));
        var result = await probe.ResolveChatIdsAsync("token", ["not-an-id", "-5364308250"], TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("not-an-id", Assert.Single(result.Unresolved));
        var chat = Assert.Single(result.Resolved);
        Assert.Equal("-5364308250", chat.ChatId);
        Assert.Equal("Netclaw group", chat.DisplayName);
    }

    [Fact]
    public async Task ResolveChatIds_rejects_private_chats_from_the_group_allow_list()
    {
        var probe = CreateProbe(_ => Json(HttpStatusCode.OK,
            """{"ok":true,"result":{"id":6875639362,"type":"private","username":"salma"}}"""));

        var result = await probe.ResolveChatIdsAsync(
            "token", ["6875639362"], TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Empty(result.Resolved);
        Assert.Equal("6875639362", Assert.Single(result.Unresolved));
    }

    [Fact]
    public async Task ResolveChatIds_uses_the_canonical_id_from_Telegram()
    {
        var probe = CreateProbe(_ => Json(HttpStatusCode.OK,
            """{"ok":true,"result":{"id":-5364308250,"type":"group","title":"Netclaw group"}}"""));

        var result = await probe.ResolveChatIdsAsync(
            "token", ["-05364308250"], TestContext.Current.CancellationToken);

        Assert.Equal("-5364308250", Assert.Single(result.Resolved).ChatId);
    }

    private static TelegramProbe CreateProbe(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new HttpClient(new StubHandler(response)));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
