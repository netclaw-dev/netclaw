// -----------------------------------------------------------------------
// <copyright file="NonShellGrantPhraseTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// <c>netclaw approvals trust-verb</c> refuses a non-shell grant when
/// <see cref="ApprovalPatternMatching.NonShellGrantPhrase"/> and
/// <see cref="ApprovalPatternMatching.MatchesAny"/> say it never matches. These
/// cases hold that answer to the real authorizer: a grant the question accepts
/// covers a call, and a grant it rejects leaves the call at its consent prompt.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class NonShellGrantPhraseTests(ShellApprovalMatrixFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("demo-utilities/calculate", true)]
    [InlineData("calculate", false)]
    [InlineData("demo-utilities", false)]
    [InlineData("demo-utilities__calculate", false)]
    [InlineData("calculate demo-utilities/calculate", false)]
    public async Task Mcp_tool_grant_covers_the_call_only_when_the_phrase_is_the_tool_name(string phrase, bool covers)
    {
        await using var harness = await CreateHarnessAsync("demo-utilities/calculate");
        var toolName = harness.RegisterMcpTool("demo-utilities", "calculate");

        await AssertAskedMatchesAuthorizerAsync(harness, toolName, new Dictionary<string, object?>(), phrase, covers);
    }

    [Fact]
    public async Task Mcp_tool_grant_with_different_case_follows_the_platform_comparison()
    {
        // Verb comparison is ordinal on POSIX and ignores case on Windows.
        const string phrase = "DEMO-UTILITIES/CALCULATE";
        await using var harness = await CreateHarnessAsync("demo-utilities/calculate");
        var toolName = harness.RegisterMcpTool("demo-utilities", "calculate");

        await AssertAskedMatchesAuthorizerAsync(
            harness,
            toolName,
            new Dictionary<string, object?>(),
            phrase,
            string.Equals(phrase, toolName, ToolApprovalEntryComparer.Comparison));
    }

    [Theory]
    [InlineData("web_fetch", true)]
    [InlineData("fetch", false)]
    [InlineData("shell_execute", false)]
    public async Task First_party_tool_grant_covers_the_call_only_when_the_phrase_is_the_tool_name(string phrase, bool covers)
    {
        await using var harness = await CreateHarnessAsync("web_fetch");

        await AssertAskedMatchesAuthorizerAsync(
            harness, "web_fetch", ToolInput.Create("Url", "https://example.com/"), phrase, covers);
    }

    private static async Task AssertAskedMatchesAuthorizerAsync(
        ShellApprovalHarness harness,
        string toolName,
        IDictionary<string, object?> arguments,
        string phrase,
        bool covers)
    {
        var before = await harness.EvaluateToolAsync(toolName, arguments, Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, before.Outcome);

        var entry = ApprovalEntry.CreateNonShell(phrase);
        var asked = ApprovalPatternMatching.MatchesAny(
            ApprovalPatternMatching.NonShellGrantPhrase(new ToolName(toolName)), [entry]);
        harness.AddStoredEntry(TrustAudience.Personal, toolName, entry);
        var after = await harness.EvaluateToolAsync(toolName, arguments, Ct);

        Assert.Equal(covers, asked);
        if (covers)
        {
            Assert.Equal(ApprovalOutcome.Allowed, after.Outcome);
            Assert.Equal(ApprovalAllowReason.StoredApproval, after.AllowReason);
        }
        else
        {
            Assert.Equal(ApprovalOutcome.RequiresApproval, after.Outcome);
        }
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(string approvalModeKey)
        => ShellApprovalHarness.CreateAsync(
            "non-shell-grant-phrase",
            new ShellApprovalInvocation("true"),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            policy: new ShellApprovalHarnessPolicy
            {
                PersonalApprovalOverrides = new Dictionary<string, ToolApprovalMode>
                {
                    [approvalModeKey] = ToolApprovalMode.Approval
                }
            });
}
