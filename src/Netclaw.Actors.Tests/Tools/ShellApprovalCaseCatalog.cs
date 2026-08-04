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
/// Defines the explicit approval-policy shape that a matrix case installs.
/// The <see cref="Missing"/> value tests the secure fallback without a policy object.
/// </summary>
internal enum ApprovalPolicyShape
{
    /// <summary>The audience profile has no explicit approval policy.</summary>
    Missing,

    /// <summary>The shell tool requires approval unless another gate grants access.</summary>
    Approval,

    /// <summary>The approval policy grants shell access without a stored approval.</summary>
    Auto,

    /// <summary>The approval policy denies shell access.</summary>
    Deny
}

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

internal sealed record ShellApprovalPolicy(
    ApprovalPolicyShape Approval,
    ShellExecutionMode ShellMode = ShellExecutionMode.HostAllowed,
    string? AdditionalSafeVerb = null)
{
    public string Display => AdditionalSafeVerb is null
        ? $"{Approval}/{ShellMode}"
        : $"{Approval}/{ShellMode}+{AdditionalSafeVerb}";
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
    ShellApprovalPolicy Policy,
    ShellApprovalInvocation Invocation,
    ApprovalState Approvals,
    ExpectedApproval Expected);

public static class ShellApprovalCases
{
    private static readonly ShellApprovalPolicy MissingPolicy = new(ApprovalPolicyShape.Missing);
    private static readonly ShellApprovalPolicy ApprovalPolicy = new(ApprovalPolicyShape.Approval);
    private static readonly ShellApprovalPolicy AutoPolicy = new(ApprovalPolicyShape.Auto);
    private static readonly ShellApprovalPolicy DenyPolicy = new(ApprovalPolicyShape.Deny);

    internal static IReadOnlyList<ShellApprovalCase> All { get; } =
    [
        Case(
            "missing-policy-prompts",
            MissingPolicy,
            Bash("git push origin dev"),
            Approvals.None,
            ExpectedApproval.Require(["git push origin dev"])),
        Case(
            "exact-approval-prompts",
            ApprovalPolicy,
            Bash("git push origin dev"),
            Approvals.None,
            ExpectedApproval.Require(["git push origin dev"])),
        Case(
            "exact-auto-allows",
            AutoPolicy,
            Bash("git push origin dev"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.PolicyAuto)),
        Case(
            "exact-deny-denies",
            DenyPolicy,
            Bash("git push origin dev"),
            Approvals.None,
            ExpectedApproval.Deny("tool_denied_by_approval_policy")),
        Case(
            "missing-policy-persistent-grant-allows",
            MissingPolicy,
            Bash("git push origin dev"),
            Approvals.PersistentAnywhere("git push origin dev"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push origin dev")),

        Case(
            "team-audience-denied",
            ApprovalPolicy,
            Bash("git push", audience: TrustAudience.Team),
            Approvals.None,
            ExpectedApproval.Deny("shell_requires_personal_context")),
        Case(
            "public-audience-denied",
            ApprovalPolicy,
            Bash("git push", audience: TrustAudience.Public),
            Approvals.None,
            ExpectedApproval.Deny("shell_requires_personal_context")),
        Case(
            "team-auto-still-denied",
            AutoPolicy,
            Bash("git push", audience: TrustAudience.Team),
            Approvals.None,
            ExpectedApproval.Deny("shell_requires_personal_context")),
        Case(
            "public-auto-still-denied",
            AutoPolicy,
            Bash("git push", audience: TrustAudience.Public),
            Approvals.None,
            ExpectedApproval.Deny("shell_requires_personal_context")),

        Case(
            "shell-off-denies",
            new ShellApprovalPolicy(ApprovalPolicyShape.Auto, ShellExecutionMode.Off),
            Bash("git status"),
            Approvals.None,
            ExpectedApproval.Deny("shell_disabled")),
        Case(
            "sandbox-only-denies",
            new ShellApprovalPolicy(ApprovalPolicyShape.Auto, ShellExecutionMode.SandboxOnly),
            Bash("git status"),
            Approvals.None,
            ExpectedApproval.Deny("shell_requires_sandbox_backend")),

        Case(
            "hard-deny-beats-approval",
            ApprovalPolicy,
            Bash("netclaw daemon stop"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-beats-auto",
            AutoPolicy,
            Bash("netclaw daemon stop"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-beats-stored-grant",
            ApprovalPolicy,
            Bash("netclaw daemon stop"),
            Approvals.PersistentAnywhere("netclaw daemon stop"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "compound-hard-deny-denies",
            AutoPolicy,
            Bash("git status && netclaw daemon stop"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),

        Case(
            "safe-verb-project-allows",
            ApprovalPolicy,
            Bash("git status"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-verb-session-allows",
            ApprovalPolicy,
            Bash("git status", ApprovalDirectoryShape.Session),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-verb-external-prompts",
            ApprovalPolicy,
            Bash("git status", ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "safe-verb-external-path-prompts",
            ApprovalPolicy,
            Bash("cat /etc/passwd"),
            Approvals.None,
            ExpectedApproval.Require(["cat"])),
        Case(
            "safe-verb-external-redirect-prompts",
            ApprovalPolicy,
            Bash("git status > /tmp/netclaw-approval-matrix.txt"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "mutating-verb-project-prompts",
            ApprovalPolicy,
            Bash("git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "all-safe-compound-allows",
            ApprovalPolicy,
            Bash("git status && git log"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "mixed-safe-unsafe-compound-prompts",
            ApprovalPolicy,
            Bash("git status && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "safe-pipe-unsafe-tail-prompts",
            ApprovalPolicy,
            Bash("git status | git push"),
            Approvals.None,
            ExpectedApproval.Require(["git status", "git push"])),
        Case(
            "added-safe-verb-project-allows",
            new ShellApprovalPolicy(ApprovalPolicyShape.Approval, AdditionalSafeVerb: "eza"),
            Bash("eza"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),

        Case(
            "echo-allows-without-grant",
            ApprovalPolicy,
            Bash("echo hello"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ApprovalExemptShellCandidates)),
        Case(
            "printf-allows-without-grant",
            ApprovalPolicy,
            Bash("printf hello"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ApprovalExemptShellCandidates)),
        Case(
            "echo-redirect-prompts",
            ApprovalPolicy,
            Bash("echo hello > result.txt"),
            Approvals.None,
            ExpectedApproval.Require(["echo"])),
        Case(
            "echo-done-fails-closed",
            ApprovalPolicy,
            Bash("echo done"),
            Approvals.None,
            ExpectedApproval.Require(["echo"], isMessy: true, approvalChecks: 0)),
        Case(
            "control-flow-fails-closed",
            ApprovalPolicy,
            Bash("for f in *.txt; do cat \"$f\"; done"),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "empty-command-fails-closed",
            ApprovalPolicy,
            Bash(string.Empty),
            Approvals.None,
            ExpectedApproval.Require([], approvalChecks: 0)),
        Case(
            "whitespace-command-fails-closed",
            ApprovalPolicy,
            Bash("   "),
            Approvals.None,
            ExpectedApproval.Require([], approvalChecks: 0)),

        Case(
            "session-grant-allows",
            ApprovalPolicy,
            Bash("git push"),
            Approvals.Session("git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "session:git push")),
        Case(
            "other-session-grant-prompts",
            ApprovalPolicy,
            Bash("git push"),
            Approvals.SessionForOtherSession("git push"),
            ExpectedApproval.Require(["git push"])),
        Case(
            "persistent-anywhere-allows",
            ApprovalPolicy,
            Bash("git push"),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push")),
        Case(
            "persistent-here-allows",
            ApprovalPolicy,
            Bash("git push"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push")),
        Case(
            "persistent-here-directory-mismatch-prompts",
            ApprovalPolicy,
            Bash("git push", ApprovalDirectoryShape.External),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git push"),
            ExpectedApproval.Require(["git push"])),
        Case(
            "other-audience-grant-prompts",
            ApprovalPolicy,
            Bash("git push"),
            Approvals.PersistentForOtherAudience("git push"),
            ExpectedApproval.Require(["git push"])),
        Case(
            "mixed-session-persistent-compound-allows",
            ApprovalPolicy,
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
            ApprovalPolicy,
            Bash("git status && git push"),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Require(
                ["git status", "git push"],
                approvalMatches: ["persistent:git status"])),

        Case(
            "noninteractive-unapproved-requires-approval",
            ApprovalPolicy,
            Bash("git push", interactive: false),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "noninteractive-persistent-grant-allows",
            ApprovalPolicy,
            Bash("git push", interactive: false),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push")),
        Case(
            "noninteractive-exempt-allows",
            ApprovalPolicy,
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
            "| ID | Policy | Audience | Cwd | Interaction | Command | Approval state | Result | Reason |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"
        };

        lines.AddRange(All.Select(testCase =>
            $"| {testCase.Id} | {testCase.Policy.Display} | " +
            $"{testCase.Invocation.Audience} | {testCase.Invocation.WorkingDirectory} | " +
            $"{(testCase.Invocation.Interactive ? "Interactive" : "Non-interactive")} | " +
            $"{Escape(testCase.Invocation.Command)} | " +
            $"{Escape(testCase.Approvals.Display)} | {testCase.Expected.Outcome} | " +
            $"{testCase.Expected.AllowReason?.ToString() ?? testCase.Expected.DenyReason ?? "approval required"} |"));

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static ShellApprovalCase Case(
        string id,
        ShellApprovalPolicy policy,
        ShellApprovalInvocation invocation,
        ApprovalState approvals,
        ExpectedApproval expected)
        => new(id, policy, invocation, approvals, expected);

    private static ShellApprovalInvocation Bash(
        string command,
        ApprovalDirectoryShape workingDirectory = ApprovalDirectoryShape.Project,
        TrustAudience audience = TrustAudience.Personal,
        bool interactive = true)
        => new(command, workingDirectory, audience, interactive);

    private static string Escape(string value)
        => value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
