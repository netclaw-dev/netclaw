// -----------------------------------------------------------------------
// <copyright file="GrantIdentityApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// One grant identity: the verb that the prompt shows is the phrase of the
/// command words that the answer saves and that a grant matches. Before this
/// rule, <c>pipedrive dealFields list</c> showed <c>pipedrive</c> and saved
/// <c>pipedrive dealFields list</c>, so the next <c>pipedrive</c> command
/// showed the same verb and prompted again.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class GrantIdentityApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ToolName Shell = new(ShellTool.ToolName);

    // The session of each ShellApprovalHarness call.
    private static readonly ToolApprovalSessionId InvocationSession = (ToolApprovalSessionId)"signalr/approval-matrix";

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(GrantScopeKind.Session)]
    [InlineData(GrantScopeKind.Folder)]
    [InlineData(GrantScopeKind.Everywhere)]
    public async Task Saved_grant_covers_the_command_that_saved_it(GrantScopeKind scope)
    {
        await using var harness = await CreateHarnessAsync();
        const string first = "pipedrive dealFields list --custom-only --json | jq '.[] | .name'";

        var prompt = await harness.EvaluateShellAsync(first, Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, prompt.Outcome);
        Assert.Contains("pipedrive dealFields list", prompt.Prompt!.CandidateVerbs);
        Assert.DoesNotContain("pipedrive", prompt.Prompt.CandidateVerbs);

        var saved = await AnswerAsync(harness, first, scope);

        Assert.Contains(saved, static grant => grant.Candidate.VerbTokens!.SequenceEqual(["pipedrive", "dealFields", "list"]));
        await AssertAllowedByStoredGrantAsync(harness, first);
        await AssertAllowedByStoredGrantAsync(harness, "pipedrive dealFields list --json");

        // Negative controls: another verb of the program, another word case,
        // and the bare program keep the prompt. The prompt names each verb.
        await AssertPromptsForAsync(harness, "pipedrive organizationFields list --json", "pipedrive organizationFields list");
        await AssertPromptsForAsync(harness, "pipedrive deals delete 42", "pipedrive deals delete");
        await AssertPromptsForAsync(harness, "pipedrive dealfields list", "pipedrive dealfields list");
        await AssertPromptsForAsync(harness, "pipedrive", "pipedrive");
    }

    /// <summary>
    /// For each candidate of each catalog command, the entry that the
    /// production save path creates has the text of the candidate verb.
    /// </summary>
    /// <remarks>
    /// What the test proves: the shown text and the saved text are equal for
    /// one candidate. The save path is <see cref="GrantBuilder"/> and
    /// <see cref="ToolApprovalActor.TryCreateEntries"/>, for the chat, the
    /// folder, and the everywhere scope. On the old code the test fails:
    /// <c>find . -exec rm {} +</c> showed <c>find</c> and saved
    /// <c>find rm {} +</c>.
    /// What the test does not prove: that the grant covers the call. A word
    /// list is a prefix of itself, so that check cannot fail here.
    /// <see cref="Saved_grant_covers_the_command_that_saved_it"/> proves it for
    /// one command through the approval actor. The repository scope is not in
    /// this test, because the catalog directories are not Git worktrees.
    /// The production code selects the candidates that save no grant: the
    /// builder drops an approval-exempt command, and the actor refuses a
    /// candidate with no command words. The test fails when a candidate that
    /// can prompt for a reusable grant saves no entry.
    /// </remarks>
    [SlopwatchSuppress("SW001", "The catalog resolves POSIX paths with the Bash grammar.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The catalog resolves POSIX paths with the Bash grammar.")]
    public void Each_catalog_candidate_saves_the_text_that_the_prompt_shows()
    {
        var saved = 0;
        var failures = new List<string>();
        foreach (var invocation in ShellApprovalCases.All
                     .Select(static item => item.Invocation)
                     .DistinctBy(static item => (item.Command, item.Host)))
        {
            var cwd = invocation.Host is ShellApprovalHost.Bash or ShellApprovalHost.Bash52
                ? "/work/project"
                : @"C:\work\project";
            var analysis = new ShellApprovalMatcher(invocation.CreateEnvironment()).AnalyzeInvocation(
                Shell,
                new Dictionary<string, object?>
                {
                    ["Command"] = invocation.Command,
                    ["WorkingDirectory"] = cwd,
                });

            foreach (var candidate in analysis.Candidates)
            {
                foreach (var kind in new[] { GrantScopeKind.Session, GrantScopeKind.Folder, GrantScopeKind.Everywhere })
                {
                    var grants = GrantBuilder.Build([candidate], kind, cwd, "/session/dir", repositoryCommonDirectory: null);
                    var created = ToolApprovalActor.TryCreateEntries(Shell, grants, out _, out var entries);
                    if (grants.Count == 0)
                    {
                        // The builder saves nothing for an approval-exempt command.
                        continue;
                    }

                    if (!created)
                    {
                        // No command words: the prompt offers no reusable grant.
                        if (candidate.VerbTokens is not null)
                            failures.Add($"[{invocation.Command}] '{candidate.Verb}' has command words and saves no {kind} entry");
                        continue;
                    }

                    saved++;
                    var entry = Assert.Single(entries);
                    if (entry.Verb != candidate.Verb)
                        failures.Add($"[{invocation.Command}] shows '{candidate.Verb}' and saves '{entry.Verb}' ({kind})");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        // The catalog must supply candidates, or the rule proves nothing.
        Assert.True(saved > 1000, $"Only {saved} entries were checked.");
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync()
        => ShellApprovalHarness.CreateAsync(
            "grant-identity",
            new ShellApprovalInvocation("true"),
            Approvals.None,
            fixture.ActorSystem,
            Ct);

    // The session actor builds and records the grants of a prompt answer.
    private static async Task<IReadOnlyList<ToolApprovalGrant>> AnswerAsync(
        ShellApprovalHarness harness,
        string command,
        GrantScopeKind scope)
    {
        var prompt = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, prompt.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(prompt.ApprovalContext);

        var grants = GrantBuilder.Build(
            approval.Candidates!,
            scope,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            InvocationSession,
            TrustAudience.Personal,
            Shell,
            grants,
            Ct);
        return grants;
    }

    private static async Task AssertAllowedByStoredGrantAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.Allowed
            && decision.AllowReason == ToolAllowReason.StoredApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}) {decision.DenyReason}; a stored grant should cover it.");
    }

    private static async Task AssertPromptsForAsync(ShellApprovalHarness harness, string command, string shownVerb)
    {
        var decision = await harness.EvaluateShellAsync(command, Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains(shownVerb, decision.Prompt!.CandidateVerbs);
    }
}
