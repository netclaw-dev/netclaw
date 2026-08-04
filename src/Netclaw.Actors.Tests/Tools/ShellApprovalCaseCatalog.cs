// -----------------------------------------------------------------------
// <copyright file="ShellApprovalCaseCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Names a logical directory that the harness resolves inside its isolated test root.
/// This type prevents a case from embedding a harness-specific temporary path.
/// </summary>
internal enum ApprovalDirectoryShape
{
    /// <summary>The case supplies no directory.</summary>
    None,

    /// <summary>The case uses the active project directory.</summary>
    Project,

    /// <summary>The case uses the active session directory.</summary>
    Session,

    /// <summary>The case uses a directory outside the project and session roots.</summary>
    External
}

/// <summary>
/// Identifies the store that owns a seeded approval.
/// The harness uses this value to select session memory or persistent storage.
/// </summary>
internal enum ApprovalSeedSource
{
    /// <summary>The approval exists only in an actor session.</summary>
    Session,

    /// <summary>The approval survives creation of a new approval actor.</summary>
    Persistent
}

/// <summary>
/// Selects the session identity for a session-scoped approval seed.
/// This axis proves that a session approval cannot authorize another session.
/// </summary>
internal enum ApprovalSessionShape
{
    /// <summary>The seed uses the session that invokes the shell tool.</summary>
    Invocation,

    /// <summary>The seed uses an unrelated session.</summary>
    Other
}

internal sealed record ShellApprovalInvocation(
    string Command,
    ApprovalDirectoryShape WorkingDirectory = ApprovalDirectoryShape.Project,
    TrustAudience Audience = TrustAudience.Personal,
    bool Interactive = true);

internal sealed record ApprovalSeed(
    ApprovalSeedSource Source,
    string Pattern,
    TrustAudience Audience,
    ApprovalSessionShape Session,
    ApprovalDirectoryShape Directory);

internal sealed record ApprovalState(IReadOnlyList<ApprovalSeed> Seeds)
{
    public static ApprovalState Empty { get; } = new([]);

    public string Display => Seeds.Count == 0
        ? "none"
        : string.Join(", ", Seeds.Select(DescribeSeed));

    private static string DescribeSeed(ApprovalSeed seed)
    {
        var source = seed.Source.ToString().ToLowerInvariant();
        var scope = seed.Source switch
        {
            ApprovalSeedSource.Session => seed.Session == ApprovalSessionShape.Invocation
                ? "this-chat"
                : "other-chat",
            ApprovalSeedSource.Persistent => seed.Directory == ApprovalDirectoryShape.None
                ? "anywhere"
                : seed.Directory.ToString().ToLowerInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(seed), seed.Source, "Unknown approval source.")
        };
        var audience = seed.Audience == TrustAudience.Personal ? string.Empty : $",{seed.Audience}";
        return $"{source}[{scope}{audience}]:{seed.Pattern}";
    }
}

internal static class Approvals
{
    public static ApprovalState None => ApprovalState.Empty;

    public static ApprovalState Session(params string[] patterns)
        => CreateSession(ApprovalSessionShape.Invocation, TrustAudience.Personal, patterns);

    public static ApprovalState SessionForOtherSession(params string[] patterns)
        => CreateSession(ApprovalSessionShape.Other, TrustAudience.Personal, patterns);

    public static ApprovalState PersistentAnywhere(params string[] patterns)
        => CreatePersistent(TrustAudience.Personal, ApprovalDirectoryShape.None, patterns);

    public static ApprovalState PersistentHere(ApprovalDirectoryShape directory, params string[] patterns)
        => CreatePersistent(TrustAudience.Personal, directory, patterns);

    public static ApprovalState PersistentForOtherAudience(params string[] patterns)
        => CreatePersistent(TrustAudience.Team, ApprovalDirectoryShape.None, patterns);

    public static ApprovalState Combine(params ApprovalState[] states)
        => new(states.SelectMany(state => state.Seeds).ToList());

    private static ApprovalState CreateSession(
        ApprovalSessionShape session,
        TrustAudience audience,
        IReadOnlyList<string> patterns)
        => new(patterns
            .Select(pattern => new ApprovalSeed(
                ApprovalSeedSource.Session,
                pattern,
                audience,
                session,
                ApprovalDirectoryShape.None))
            .ToList());

    private static ApprovalState CreatePersistent(
        TrustAudience audience,
        ApprovalDirectoryShape directory,
        IReadOnlyList<string> patterns)
        => new(patterns
            .Select(pattern => new ApprovalSeed(
                ApprovalSeedSource.Persistent,
                pattern,
                audience,
                ApprovalSessionShape.Invocation,
                directory))
            .ToList());
}

internal sealed record ExpectedApproval(
    ToolAuthorizationOutcome Outcome,
    ToolAllowReason? AllowReason,
    string? DenyReason,
    IReadOnlyList<string> Candidates,
    bool? IsMessy,
    int ApprovalChecks,
    IReadOnlyList<string> ApprovalMatches)
{
    public static ExpectedApproval Allow(
        ToolAllowReason reason,
        int approvalChecks = 0,
        params string[] approvalMatches)
        => new(
            ToolAuthorizationOutcome.Allowed,
            reason,
            null,
            [],
            null,
            approvalChecks,
            approvalMatches);

    public static ExpectedApproval Require(
        IReadOnlyList<string> candidates,
        bool isMessy = false,
        int approvalChecks = 1,
        params string[] approvalMatches)
        => new(
            ToolAuthorizationOutcome.RequiresApproval,
            null,
            null,
            candidates,
            isMessy,
            approvalChecks,
            approvalMatches);

    public static ExpectedApproval Deny(string reason)
        => new(
            ToolAuthorizationOutcome.Denied,
            null,
            reason,
            [],
            null,
            0,
            []);
}

internal sealed record ShellApprovalCase(
    string Id,
    ShellApprovalInvocation Invocation,
    ApprovalState Approvals,
    ExpectedApproval Expected);

public static class ShellApprovalCases
{
    internal static IReadOnlyList<ShellApprovalCase> All { get; } =
    [
        Case(
            "mutating-command-prompts",
            Bash("git push origin dev"),
            Approvals.None,
            ExpectedApproval.Require(["git push origin dev"])),

        Case(
            "team-audience-denied",
            Bash("git push", audience: TrustAudience.Team),
            Approvals.None,
            ExpectedApproval.Deny("tool_not_allowed_for_audience_profile")),
        Case(
            "public-audience-denied",
            Bash("git push", audience: TrustAudience.Public),
            Approvals.None,
            ExpectedApproval.Deny("tool_not_allowed_for_audience_profile")),

        Case(
            "hard-deny-blocks",
            Bash("netclaw daemon stop"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-beats-stored-grant",
            Bash("netclaw daemon stop"),
            Approvals.PersistentAnywhere("netclaw daemon stop"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "compound-hard-deny-denies",
            Bash("git status && netclaw daemon stop"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),

        Case(
            "safe-verb-project-allows",
            Bash("git status"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-verb-session-allows",
            Bash("git status", ApprovalDirectoryShape.Session),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-verb-external-prompts",
            Bash("git status", ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "safe-verb-external-path-prompts",
            Bash("cat /etc/passwd"),
            Approvals.None,
            ExpectedApproval.Require(["cat"])),
        Case(
            "safe-verb-external-redirect-prompts",
            Bash($"git status > {TemporaryFile("netclaw-approval-matrix.txt")}"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "mutating-verb-project-prompts",
            Bash("git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "all-safe-compound-allows",
            Bash("git status && git log"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "mixed-safe-unsafe-compound-prompts",
            Bash("git status && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "safe-pipe-unsafe-tail-prompts",
            Bash("git status | git push"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "safe-pipeline-allows",
            Bash("git log | head -20"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "semicolon-sequence-prompts",
            Bash("git status; git push"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "newline-sequence-prompts",
            Bash("git status\ngit push"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "or-chain-prompts",
            Bash("git status || git push"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "three-step-release-prompts",
            Bash("git add . && git commit -m fix && git push origin dev"),
            Approvals.None,
            ExpectedApproval.Require(["git add", "git commit", "git push origin dev"])),
        Case(
            "hard-deny-pipeline-tail-currently-prompts",
            Bash("echo safe | netclaw daemon stop"),
            Approvals.None,
            ExpectedApproval.Require(["echo", "netclaw daemon stop"])),
        Case(
            "hard-deny-nested-shell-blocks",
            Bash("bash -lc \"netclaw daemon stop\""),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "nested-shell-currently-prompts-for-wrapper",
            Bash("bash -lc \"git push\""),
            Approvals.None,
            ExpectedApproval.Require(["bash"])),
        Case(
            "nested-shell-inner-grant-currently-does-not-match",
            Bash("bash -lc \"git push\""),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Require(["bash"])),
        Case(
            "nested-shell-wrapper-grant-currently-allows",
            Bash("bash -lc \"git push\""),
            Approvals.PersistentAnywhere("bash"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:bash")),
        Case(
            "env-nested-shell-prompts",
            Bash("env bash -lc \"git push\""),
            Approvals.None,
            ExpectedApproval.Require(["env bash"])),
        Case(
            "timeout-nested-shell-prompts",
            Bash("timeout 5 bash -lc \"git push\""),
            Approvals.None,
            ExpectedApproval.Require(["timeout"])),
        Case(
            "subshell-prompts",
            Bash("(git status && git push)"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "command-substitution-currently-auto-allows",
            Bash("echo $(git push)"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ApprovalExemptShellCandidates)),
        Case(
            "background-list-currently-auto-allows",
            Bash("git status & git push"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "unbalanced-quote-fails-closed",
            Bash("git push \"unterminated"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "multiline-argument-prompts",
            Bash("gh issue comment 123 --body \"first line\nsecond line\""),
            Approvals.None,
            ExpectedApproval.Require(["gh issue comment"])),
        Case(
            "approved-pipeline-head-does-not-cover-tail",
            Bash("git push | curl https://example.com"),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Require(
                ["git push", "curl"],
                approvalMatches: ["persistent:git push"])),
        Case(
            "all-pipeline-clauses-approved",
            Bash("git push | curl https://example.com"),
            Approvals.PersistentAnywhere("git push", "curl"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git push",
                "persistent:curl")),
        Case(
            "input-redirect-outside-zone-prompts",
            Bash("cat < /etc/passwd"),
            Approvals.None,
            ExpectedApproval.Require(["cat"])),
        Case(
            "error-redirect-outside-zone-prompts",
            Bash($"git status 2> {TemporaryFile("netclaw-approval-errors.txt")}"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "cd-current-then-safe-allows",
            Bash("cd . && git status"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "cd-parent-then-safe-prompts",
            Bash("cd .. && git status"),
            Approvals.None,
            ExpectedApproval.Require(["cd", "git status"])),
        Case(
            "multiple-cd-then-safe-prompts",
            Bash("cd . && cd .. && git status"),
            Approvals.None,
            ExpectedApproval.Require(["cd", "git status"])),
        Case(
            "side-effect-before-mutation-prompts",
            Bash("echo ready && git push"),
            Approvals.None,
            ExpectedApproval.Require(["echo", "git push"])),
        Case(
            "heredoc-prompts",
            Bash("cat <<'EOF'\nhello\nEOF"),
            Approvals.None,
            ExpectedApproval.Require([], approvalChecks: 0)),

        Case(
            "echo-allows-without-grant",
            Bash("echo hello"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ApprovalExemptShellCandidates)),
        Case(
            "printf-allows-without-grant",
            Bash("printf hello"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ApprovalExemptShellCandidates)),
        Case(
            "echo-redirect-prompts",
            Bash("echo hello > result.txt"),
            Approvals.None,
            ExpectedApproval.Require(["echo"])),
        Case(
            "echo-done-fails-closed",
            Bash("echo done"),
            Approvals.None,
            ExpectedApproval.Require(["echo"], isMessy: true, approvalChecks: 0)),
        Case(
            "control-flow-fails-closed",
            Bash("for f in *.txt; do cat \"$f\"; done"),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "empty-command-fails-closed",
            Bash(string.Empty),
            Approvals.None,
            ExpectedApproval.Require([], approvalChecks: 0)),
        Case(
            "whitespace-command-fails-closed",
            Bash("   "),
            Approvals.None,
            ExpectedApproval.Require([], approvalChecks: 0)),

        Case(
            "session-grant-allows",
            Bash("git push"),
            Approvals.Session("git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "session:git push")),
        Case(
            "other-session-grant-prompts",
            Bash("git push"),
            Approvals.SessionForOtherSession("git push"),
            ExpectedApproval.Require(["git push"])),
        Case(
            "persistent-anywhere-allows",
            Bash("git push"),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push")),
        Case(
            "persistent-here-allows",
            Bash("git push"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push")),
        Case(
            "persistent-here-directory-mismatch-prompts",
            Bash("git push", ApprovalDirectoryShape.External),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git push"),
            ExpectedApproval.Require(["git push"])),
        Case(
            "other-audience-grant-prompts",
            Bash("git push"),
            Approvals.PersistentForOtherAudience("git push"),
            ExpectedApproval.Require(["git push"])),
        Case(
            "mixed-session-persistent-compound-allows",
            Bash("git status && git push"),
            Approvals.Combine(
                Approvals.Session("git status"),
                Approvals.PersistentAnywhere("git push")),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "session:git status",
                "persistent:git push")),
        Case(
            "partial-compound-grant-prompts",
            Bash("git status && git push"),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Require(
                ["git status", "git push"],
                approvalMatches: ["persistent:git status"])),

        Case(
            "noninteractive-unapproved-requires-approval",
            Bash("git push", interactive: false),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "noninteractive-persistent-grant-allows",
            Bash("git push", interactive: false),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push")),
        Case(
            "noninteractive-exempt-allows",
            Bash("echo hello", interactive: false),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ApprovalExemptShellCandidates))
    ];

    private static readonly FrozenDictionary<string, ShellApprovalCase> CasesById =
        All.ToFrozenDictionary(testCase => testCase.Id, StringComparer.Ordinal);

    public static IEnumerable<TheoryDataRow<string>> Rows => All.Select(testCase =>
        new TheoryDataRow<string>(testCase.Id)
            .WithTestDisplayName($"shell approval :: {testCase.Id}")
            .WithTrait("Disposition", testCase.Expected.Outcome.ToString())
            .WithTrait("AllowReason", testCase.Expected.AllowReason?.ToString() ?? "NotAllowed"));

    internal static ShellApprovalCase Get(string id) => CasesById[id];

    internal static string RenderReviewTable()
    {
        var lines = new List<string>
        {
            "# Fresh Personal approval matrix",
            string.Empty,
            "`Tools.ShellMode`: `HostAllowed`",
            string.Empty,
            "`Personal.ApprovalPolicy.shell_execute`: `Approval`",
            string.Empty,
            "| ID | Audience | Cwd | Interaction | Command | Approval state | Result | Reason | Candidates | Complex |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"
        };

        lines.AddRange(All.Select(testCase =>
            $"| {testCase.Id} | {testCase.Invocation.Audience} | {testCase.Invocation.WorkingDirectory} | " +
            $"{(testCase.Invocation.Interactive ? "Interactive" : "Non-interactive")} | " +
            $"{Escape(testCase.Invocation.Command)} | " +
            $"{Escape(testCase.Approvals.Display)} | {testCase.Expected.Outcome} | " +
            $"{testCase.Expected.AllowReason?.ToString() ?? testCase.Expected.DenyReason ?? "approval required"} | " +
            $"{Escape(DisplayCandidates(testCase.Expected.Candidates))} | {DisplayComplexity(testCase.Expected.IsMessy)} |"));

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static ShellApprovalCase Case(
        string id,
        ShellApprovalInvocation invocation,
        ApprovalState approvals,
        ExpectedApproval expected)
        => new(id, invocation, approvals, expected);

    private static ShellApprovalInvocation Bash(
        string command,
        ApprovalDirectoryShape workingDirectory = ApprovalDirectoryShape.Project,
        TrustAudience audience = TrustAudience.Personal,
        bool interactive = true)
        => new(command, workingDirectory, audience, interactive);

    private static string TemporaryFile(string fileName)
        => Path.Join(Path.GetTempPath(), fileName);

    private static string Escape(string value)
        => value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string DisplayCandidates(IReadOnlyList<string> candidates)
        => candidates.Count == 0 ? "none" : string.Join(", ", candidates);

    private static string DisplayComplexity(bool? isMessy)
        => isMessy switch
        {
            true => "Yes",
            false => "No",
            null => "Not applicable"
        };
}
