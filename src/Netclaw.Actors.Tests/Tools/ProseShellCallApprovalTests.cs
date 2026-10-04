// -----------------------------------------------------------------------
// <copyright file="ProseShellCallApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// A model can send plain prose to <c>shell_execute</c>. Prose is ordinary
/// shell input: a chat gets a normal prompt, and an unattended run gets a
/// normal denial. Before #2336, a quoted span with spaces (the text between
/// two apostrophes) made the coordinator fail with <c>internal_policy_failure</c>.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ProseShellCallApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    public static TheoryData<string, bool> ProseCases()
    {
        string[] prose =
        [
            "I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me?",
            "Can you check what's wrong with the build?",
            "Please summarize the last three commits",
            "Book a flight from IAH to CMH & a hotel near the venue",
            "Remind me tomorrow: call Bob; then email Alice",
        ];
        var cases = new TheoryData<string, bool>();
        foreach (var text in prose)
        {
            cases.Add(text, true);
            cases.Add(text, false);
        }

        return cases;
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [MemberData(nameof(ProseCases))]
    public Task Bash_prose_gets_a_normal_outcome(string prose, bool interactive)
        => AssertNormalOutcomeAsync(prose, interactive, ShellApprovalHost.Bash);

    [Theory]
    [MemberData(nameof(ProseCases))]
    public Task PowerShell_prose_gets_a_normal_outcome(string prose, bool interactive)
        => AssertNormalOutcomeAsync(prose, interactive, ShellApprovalHost.PowerShell7);

    private async Task AssertNormalOutcomeAsync(string prose, bool interactive, ShellApprovalHost host)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "prose-shell-call",
            new ShellApprovalInvocation("true", Interactive: interactive, Host: host),
            Approvals.None,
            fixture.ActorSystem,
            ct);

        var observed = await harness.EvaluateShellAsync(prose, ct);

        Assert.NotEqual("internal_policy_failure", observed.DenyReason);
        if (interactive)
        {
            // The person in the chat sees the prose and can deny it.
            Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
            Assert.Equal(prose, observed.Prompt?.DisplayText);
            return;
        }

        // Nobody can answer in an unattended run, and no grant covers prose,
        // so the call never runs.
        Assert.Contains(observed.Outcome, new[] { ApprovalOutcome.Denied, ApprovalOutcome.RequiresApproval });
    }
}
