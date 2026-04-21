using Netclaw.Actors.Protocol;
using Xunit;

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
    [InlineData("d", ApprovalOptionKeys.Deny)]
    [InlineData("4", ApprovalOptionKeys.Deny)]
    [InlineData("reject", ApprovalOptionKeys.Deny)]
    public void Parses_deterministic_approval_keywords(string input, string expected)
    {
        var ok = ToolInteractionResponseParser.TryParseApprovalResponse(input, out var selectedKey);

        Assert.True(ok);
        Assert.Equal(expected, selectedKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("maybe")]
    [InlineData("approve later")]
    public void Rejects_unrecognized_responses(string input)
    {
        var ok = ToolInteractionResponseParser.TryParseApprovalResponse(input, out var selectedKey);

        Assert.False(ok);
        Assert.Null(selectedKey);
    }
}
