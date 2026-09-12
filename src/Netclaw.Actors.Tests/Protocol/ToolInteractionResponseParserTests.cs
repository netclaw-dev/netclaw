// -----------------------------------------------------------------------
// <copyright file="ToolInteractionResponseParserTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class ToolInteractionResponseParserTests
{
    [Theory]
    [InlineData("a", ApprovalOptionKeys.ApproveOnce)]
    [InlineData("A", ApprovalOptionKeys.ApproveOnce)]
    [InlineData("1", ApprovalOptionKeys.ApproveOnce)]
    [InlineData("approve once", ApprovalOptionKeys.ApproveOnce)]
    [InlineData("b", ApprovalOptionKeys.ApproveSession)]
    [InlineData("2", ApprovalOptionKeys.ApproveSession)]
    [InlineData("this thread", ApprovalOptionKeys.ApproveSession)]
    [InlineData("c", ApprovalOptionKeys.ApproveAlways)]
    [InlineData("3", ApprovalOptionKeys.ApproveAlways)]
    [InlineData("always", ApprovalOptionKeys.ApproveAlways)]
    [InlineData("d", ApprovalOptionKeys.ApproveEverywhere)]
    [InlineData("4", ApprovalOptionKeys.ApproveEverywhere)]
    [InlineData("approve everywhere", ApprovalOptionKeys.ApproveEverywhere)]
    [InlineData("always anywhere", ApprovalOptionKeys.ApproveEverywhere)]
    [InlineData("e", ApprovalOptionKeys.Deny)]
    [InlineData("5", ApprovalOptionKeys.Deny)]
    [InlineData("reject", ApprovalOptionKeys.Deny)]
    public void Parses_deterministic_approval_keywords(string input, string expected)
    {
        var ok = ToolInteractionResponseParser.TryParseApprovalResponse(input, CreateFiveButtonOptions(), out var selectedKey);

        Assert.True(ok);
        Assert.Equal(expected, selectedKey);
    }

    [Fact]
    public void Letter_mapping_uses_visible_option_order_when_always_here_is_pruned()
    {
        var ok = ToolInteractionResponseParser.TryParseApprovalResponse(
            "c",
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ],
            out var selectedKey);

        Assert.True(ok);
        Assert.Equal(ApprovalOptionKeys.ApproveEverywhere, selectedKey);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("5")]
    [InlineData("approve everywhere")]
    public void LooksLikeApprovalResponse_accepts_common_cold_path_inputs(string input)
        => Assert.True(ToolInteractionResponseParser.LooksLikeApprovalResponse(input));

    [Theory]
    [InlineData("")]
    [InlineData("maybe later")]
    [InlineData("ship it")]
    public void LooksLikeApprovalResponse_rejects_normal_chat_text(string input)
        => Assert.False(ToolInteractionResponseParser.LooksLikeApprovalResponse(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("maybe")]
    [InlineData("approve later")]
    [InlineData("approve everywhere")]
    public void Rejects_unrecognized_responses(string input)
    {
        var ok = ToolInteractionResponseParser.TryParseApprovalResponse(
            input,
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ],
            out var selectedKey);

        Assert.False(ok);
        Assert.Null(selectedKey);
    }

    private static IReadOnlyList<ToolInteractionOption> CreateFiveButtonOptions()
        =>
        [
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
            new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ];
}
