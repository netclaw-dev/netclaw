// -----------------------------------------------------------------------
// <copyright file="AuthorizationAttemptIdTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Tools;

public sealed class AuthorizationAttemptIdTests
{
    [Fact]
    public void New_ids_are_canonical_parseable_and_unique()
    {
        var first = AuthorizationAttemptId.New();
        var second = AuthorizationAttemptId.New();

        Assert.StartsWith("auth-", first.Value, StringComparison.Ordinal);
        Assert.Equal(37, first.Value.Length);
        Assert.True(AuthorizationAttemptId.TryParse(first.Value, out var parsed));
        Assert.Equal(first, parsed);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("call-123")]
    [InlineData("auth-not-a-guid")]
    [InlineData("AUTH-0123456789abcdef0123456789abcdef")]
    public void Invalid_values_do_not_parse(string? value)
    {
        Assert.False(AuthorizationAttemptId.TryParse(value, out _));
    }

    [Fact]
    public void Legacy_pending_approval_gets_fresh_diagnostic_identity()
    {
        var legacy = new ToolApprovalRequested
        {
            CallId = "legacy-call",
            ToolName = "shell_execute"
        };

        var restored = ToolApprovalTurnContext.RestoreAuthorizationAttemptId(legacy);

        Assert.True(AuthorizationAttemptId.TryParse(restored.Value, out _));
        Assert.NotEqual(legacy.CallId, restored.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-authorization-attempt")]
    [InlineData("auth-0123456789abcdef0123456789abcdeg")]
    public void Malformed_pending_approval_gets_fresh_diagnostic_identity(string malformedValue)
    {
        var pending = new ToolApprovalRequested
        {
            CallId = "malformed-call",
            ToolName = "shell_execute",
            AuthorizationAttemptId = malformedValue
        };

        var restored = ToolApprovalTurnContext.RestoreAuthorizationAttemptId(pending);

        Assert.True(AuthorizationAttemptId.TryParse(restored.Value, out _));
        Assert.NotEqual(malformedValue, restored.Value);
    }

    [Fact]
    public void Current_pending_approval_preserves_diagnostic_identity()
    {
        var expected = AuthorizationAttemptId.New();
        var current = new ToolApprovalRequested
        {
            CallId = "current-call",
            ToolName = "shell_execute",
            AuthorizationAttemptId = expected.Value
        };

        var restored = ToolApprovalTurnContext.RestoreAuthorizationAttemptId(current);

        Assert.Equal(expected, restored);
    }
}
