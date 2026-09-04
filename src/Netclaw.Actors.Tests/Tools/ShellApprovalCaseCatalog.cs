// -----------------------------------------------------------------------
// <copyright file="ShellApprovalCaseCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using ShellSyntaxTree;
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

internal enum ShellApprovalHost
{
    Bash,
    PowerShell7,
    WindowsPowerShell51
}

internal sealed record ShellApprovalInvocation(
    string Command,
    ApprovalDirectoryShape WorkingDirectory = ApprovalDirectoryShape.Project,
    TrustAudience Audience = TrustAudience.Personal,
    bool Interactive = true,
    ShellApprovalHost Host = ShellApprovalHost.Bash)
{
    public ShellExecutionEnvironment CreateEnvironment()
        => Host switch
        {
            ShellApprovalHost.Bash => ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            ShellApprovalHost.PowerShell7 => ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7),
            ShellApprovalHost.WindowsPowerShell51 => ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                PwshDialect.WindowsPowerShell51),
            _ => throw new ArgumentOutOfRangeException(nameof(Host), Host, "Unknown shell approval host.")
        };
}

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
        int? approvalChecks = null,
        params string[] approvalMatches)
        => new(
            ToolAuthorizationOutcome.Allowed,
            reason,
            null,
            [],
            null,
            approvalChecks ?? (reason == ToolAllowReason.ReviewedSafePolicy ? 1 : 0),
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
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "safe-git-ls-tree-ref-allows",
            Bash("git ls-tree feature"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "safe-git-ls-tree-external-prompts-with-canonical-verb",
            Bash("git ls-tree feature", ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["git ls-tree feature"])),
        Case(
            "safe-git-ls-tree-external-reuses-canonical-grant",
            Bash("git ls-tree feature", ApprovalDirectoryShape.External),
            Approvals.PersistentHere(ApprovalDirectoryShape.External, "git ls-tree"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git ls-tree feature")),
        Case(
            "safe-verb-context-project-fallback-allows",
            Bash("cat src/readme.txt", ApprovalDirectoryShape.None),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "safe-verb-context-project-traversal-prompts",
            Bash("cat ../secret.txt", ApprovalDirectoryShape.None),
            Approvals.None,
            ExpectedApproval.Require(["cat"])),
        Case(
            "safe-verb-session-allows",
            Bash("git status", ApprovalDirectoryShape.Session),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
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
            "safe-verb-quoted-external-path-prompts",
            Bash("cat \"/etc/netclaw.secret\""),
            Approvals.None,
            ExpectedApproval.Require(["cat"])),
        Case(
            "safe-verb-traversal-external-path-prompts",
            Bash("cat safe/../../../../../../etc/netclaw.secret"),
            Approvals.None,
            ExpectedApproval.Require(["cat"])),
        Case(
            "safe-verb-bash-provider-looking-relative-path-allows",
            Bash("cat filesystem::/etc/netclaw.secret"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "safe-verb-external-redirect-prompts",
            Bash($"git status > {TemporaryFile("netclaw-approval-matrix.txt")}"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "safe-verb-null-device-redirect-prompts",
            Bash("ls -la 2>/dev/null"),
            Approvals.None,
            ExpectedApproval.Require(["ls"])),
        Case(
            "mutating-verb-project-prompts",
            Bash("git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "all-safe-compound-allows",
            Bash("git status && git ls-tree HEAD"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "four-safe-mixed-operator-clauses-allow",
            Bash("git status && git ls-tree HEAD | head -20; pwd"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "mixed-safe-unsafe-compound-prompts",
            Bash("git status && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "safe-pipe-unsafe-tail-prompts",
            Bash("git status | git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "safe-pipeline-allows",
            Bash("git ls-tree HEAD | head -20"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "unsafe-catalog-find-exec-prompts",
            Bash("find . -exec rm {} +"),
            Approvals.None,
            ExpectedApproval.Require(["find"])),
        Case(
            "unsafe-catalog-awk-system-prompts",
            Bash("awk 'BEGIN { system(\"touch marker\") }'"),
            Approvals.None,
            ExpectedApproval.Require(["awk"])),
        Case(
            "unsafe-catalog-rg-pre-prompts",
            Bash("rg --pre helper pattern ."),
            Approvals.None,
            ExpectedApproval.Require(["rg"])),
        Case(
            "unsafe-catalog-sort-output-prompts",
            Bash("sort -o output input"),
            Approvals.None,
            ExpectedApproval.Require(["sort"])),
        Case(
            "unsafe-catalog-date-set-prompts",
            Bash("date --set tomorrow"),
            Approvals.None,
            ExpectedApproval.Require(["date"])),
        Case(
            "unsafe-catalog-tree-output-prompts",
            Bash("tree -o output"),
            Approvals.None,
            ExpectedApproval.Require(["tree"])),
        Case(
            "unsafe-catalog-uniq-output-prompts",
            Bash("uniq input output"),
            Approvals.None,
            ExpectedApproval.Require(["uniq input output"])),
        Case(
            "unsafe-catalog-gh-web-prompts",
            Bash("gh run view 123456 --web"),
            Approvals.None,
            ExpectedApproval.Require(["gh run view"])),
        Case(
            "reviewed-git-global-option-before-phrase-prompts",
            Bash("git -c include.path=/tmp/external status"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "reviewed-grep-external-option-path-prompts",
            Bash("grep -f /tmp/patterns ./data.txt"),
            Approvals.None,
            ExpectedApproval.Require(["grep"])),
        Case(
            "reviewed-wc-external-option-path-prompts",
            Bash("wc --files0-from=/tmp/list"),
            Approvals.None,
            ExpectedApproval.Require(["wc"])),
        Case(
            "reviewed-du-external-option-path-prompts",
            Bash("du --exclude-from=/tmp/patterns ./data"),
            Approvals.None,
            ExpectedApproval.Require(["du"])),
        Case(
            "reviewed-realpath-external-option-path-prompts",
            Bash("realpath --relative-to=/tmp ./data"),
            Approvals.None,
            ExpectedApproval.Require(["realpath"])),
        Case(
            "reviewed-grep-local-option-path-allows",
            Bash("grep -f ./patterns ./data.txt"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "reviewed-path-shaped-data-under-project-allows",
            Bash("gh run list --repo example/project"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),

        Case(
            "live-read-chain-with-separator-prompts-for-rg",
            Bash("rg -rn \"operation failed\" src/ tests/ | head -20; echo \"---\"; rg -rln \"upload\" src/ | head -20"),
            Approvals.None,
            ExpectedApproval.Require(["rg"])),

        Case(
            "live-git-diagnostic-chain-prompts-for-unproved-phrases",
            Bash("git status --short 2>&1 | head; echo \"---branch---\"; git branch --show-current 2>&1; echo \"---remotes---\"; git remote -v 2>&1 | head -4; echo \"---recent---\"; git log --oneline -3 2>&1"),
            Approvals.None,
            ExpectedApproval.Require(["git branch", "git remote", "git log"])),

        Case(
            "live-finite-url-loop-prompts-with-reusable-phrase",
            Bash("for url in /api/first /api/second; do echo \"=== $url ===\"; curl -sS -m 10 \"$url\" | head -c 1500; echo; done"),
            Approvals.None,
            ExpectedApproval.Require(["curl"], isMessy: false)),

        Case(
            "gh-run-diagnostic-exit-status-prompts-without-grant",
            Bash(
                "gh run view 123456 --repo example/project --log-failed --verbose 2>&1 "
                + "| head -200; echo \"---EXIT $?---\""),
            Approvals.None,
            ExpectedApproval.Require(["gh run view"])),

        Case(
            "live-finite-run-loop-with-tr-data-reuses-gh-grant",
            Bash(
                "for r in 100001 100002 100003 100004 100005; do "
                + "echo -n \"$r: \"; "
                + "gh run view $r --json headSha,headBranch,displayTitle 2>/dev/null "
                + "| tr -d '\\n'; echo; done"),
            Approvals.PersistentAnywhere("gh run view"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:gh run view")),

        Case(
            "live-inline-cd-mixed-read-chain-remains-complex",
            Bash(
                "cd /work/netclaw-worktrees/fix-probe-timeout "
                + "&& sed -n '40,80p' src/Netclaw.Daemon/Probe.cs; "
                + "echo \"=== TESTS ===\"; "
                + "ls src/Netclaw.Daemon.Tests/ | grep -i powershell; "
                + "grep -rn \"ProbeTimeout\\|WaitForExitAsync\" "
                + "src/Netclaw.Daemon.Tests/ProbeTests.cs 2>/dev/null | head"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),

        Case(
            "post-334cb4c-independent-read-batch-remains-complex",
            Bash(
                "grep -n \"Alpha\" src/Alpha.cs | head -5; "
                + "grep -rn \"Beta\" src/*.cs tests/*.cs docs/*.md 2>/dev/null | head"),
            Approvals.None,
            ExpectedApproval.Require(["grep"])),

        Case(
            "post-334cb4c-inline-cd-read-batch-remains-complex",
            Bash(
                "cd /work/project && git log --oneline -5 -- src/Alpha.cs "
                + "&& grep -n \"Timeout\" src/Alpha.cs tests/AlphaTests.cs 2>/dev/null | head -5; "
                + "cat Project.csproj"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),

        Case(
            "live-typed-cwd-mixed-read-chain-prompts-for-sed-and-pattern",
            Bash(
                "sed -n '40,80p' src/Netclaw.Daemon/Probe.cs; "
                + "echo \"=== TESTS ===\"; "
                + "ls src/Netclaw.Daemon.Tests/ | grep -i powershell; "
                + "grep -rn \"ProbeTimeout\\|WaitForExitAsync\" "
                + "src/Netclaw.Daemon.Tests/ProbeTests.cs 2>/dev/null | head"),
            Approvals.None,
            ExpectedApproval.Require(["sed", "grep"])),

        Case(
            "native-project-path-operand-prompts-for-unproved-verb",
            Bash("git diff install-skills.sh"),
            Approvals.None,
            ExpectedApproval.Require(["git diff"])),
        Case(
            "native-external-path-operand-prompts",
            Bash("git diff /etc/passwd"),
            Approvals.None,
            ExpectedApproval.Require(["git diff"])),
        Case(
            "native-project-path-operand-reuses-grant",
            Bash("kubectl apply deployment.yaml"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "kubectl apply"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:kubectl apply")),
        Case(
            "native-external-path-operand-does-not-reuse-project-grant",
            Bash("kubectl apply /etc/deployment.yaml"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "kubectl apply"),
            ExpectedApproval.Require(["kubectl apply"])),
        Case(
            "native-output-option-outside-scope-prompts",
            Bash("curl -D /etc/netclaw.headers https://example.invalid/api"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Require(["curl"])),
        Case(
            "native-command-valued-option-fails-closed",
            Bash("tar --info-script=./helper.sh archive.tar"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "tar"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "native-project-file-reference-reuses-grant",
            Bash("curl --data=@request.json https://example.invalid/api"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:curl")),
        Case(
            "native-external-file-reference-prompts",
            Bash("curl --data=@/etc/passwd https://example.invalid/api"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Require(["curl"])),
        Case(
            "native-later-external-path-prompts",
            Bash("curl -D ./headers.txt --data=@/etc/passwd https://example.invalid/api"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Require(["curl"], approvalMatches: ["persistent:curl"])),
        Case(
            "native-earlier-external-path-prompts",
            Bash("curl -D /etc/netclaw.headers --data=@request.json https://example.invalid/api"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Require(["curl"], approvalMatches: ["persistent:curl"])),
        Case(
            "native-two-project-paths-reuse-grant",
            Bash("curl -D ./headers.txt --data=@request.json https://example.invalid/api"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:curl")),
        Case(
            "native-option-and-redirect-scopes-all-checked",
            Bash("curl --data=@/etc/passwd https://example.invalid/api > ./response.json"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Require(["curl"], approvalMatches: ["persistent:curl"])),
        Case(
            "native-dynamic-file-reference-fails-closed",
            Bash("curl --data=@$REQUEST_FILE https://example.invalid/api"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "curl"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "local-glob-allows-safe-verb",
            Bash("ls *.txt"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "local-glob-reuses-project-grant",
            Bash("rm *.tmp"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "rm"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:rm")),
        Case(
            // Use an isolated temp subdirectory as the covering directory, not
            // the shared system temp root: a symlink child there (e.g. an IDE
            // socket) trips ContainsSymlinkEntry and fails the glob closed,
            // which is correct behavior but not what this case exercises.
            "external-glob-does-not-reuse-project-grant",
            Bash($"rm {TemporaryFile("netclaw-ext-glob/*.bak")}"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "rm"),
            ExpectedApproval.Require(["rm"])),
        Case(
            "glob-traversal-fails-closed",
            Bash("cat */../../secret.txt"),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "glob-intermediate-symlink-scope-fails-closed",
            Bash("cat artifacts/*/secret.txt"),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        // Directory-listing idiom `foo/*/`: a trailing slash filters the glob to
        // directories but stays a direct-child scope, so it is NOT a "complex
        // command". Inside the trusted tree a read-only safe verb auto-allows
        // (silent, no prompt) exactly like the leaf glob `ls *.txt`.
        Case(
            "directory-listing-glob-in-project-auto-allows",
            Bash("ls -d subdirs/*/"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        // Outside the trusted tree the same command prompts — but now with a
        // persistent grant scoped to the covering directory, not one-shot only.
        // This is the reported regression (0.25.3 flipped it to complex-command).
        Case(
            "directory-listing-glob-external-offers-persistent-grant",
            Bash("ls -d subdirs/*/", ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["ls"], isMessy: false)),
        // The exact reported command: the pipe folds into one approval unit and
        // the directory glob no longer forces the whole pipeline one-shot.
        Case(
            "directory-listing-glob-pipeline-offers-persistent-grant",
            Bash("ls -d subdirs/*/ | xargs -n1 basename", ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["ls", "xargs"], isMessy: false)),
        Case(
            "native-global-option-identity-gap-currently-prompts",
            Bash("git --no-pager status"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git status"),
            ExpectedApproval.Require(["git"])),

        Case(
            "semicolon-sequence-prompts",
            Bash("git status; git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "newline-sequence-prompts",
            Bash("git status\ngit push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "or-chain-prompts",
            Bash("git status || git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "three-step-release-prompts",
            Bash("git add . && git commit -m fix && git push origin dev"),
            Approvals.None,
            ExpectedApproval.Require(["git add", "git commit", "git push origin dev"])),
        Case(
            "hard-deny-pipeline-tail-blocks",
            Bash("echo safe | netclaw daemon stop"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-nested-shell-blocks",
            Bash("bash -lc \"netclaw daemon stop\""),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-sudo-nested-shell-blocks",
            Bash("sudo bash -lc \"git status\""),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_privilege_escalation")),
        Case(
            "hard-deny-dash-shell-blocks",
            Bash("/bin/dash -c \"netclaw daemon stop\""),
            Approvals.PersistentAnywhere("/bin/dash"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "nested-shell-prompts-for-inner-command",
            Bash("bash -lc \"git push\""),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "nested-shell-inner-grant-allows",
            Bash("bash -lc \"git push\""),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:git push")),
        Case(
            "nested-shell-wrapper-grant-does-not-cover-inner-command",
            Bash("bash -lc \"git push\""),
            Approvals.PersistentAnywhere("bash"),
            ExpectedApproval.Require(["git push"])),
        Case(
            "bash-treats-pwsh-payload-as-ordinary-argument",
            Bash("pwsh -NoProfile -Command 'Get-Content ./a.txt'"),
            Approvals.None,
            ExpectedApproval.Require(["pwsh"])),
        Case(
            "bash-pwsh-grant-covers-authored-external-command",
            Bash("pwsh -NoProfile -Command 'git push'"),
            Approvals.PersistentAnywhere("pwsh"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:pwsh")),
        Case(
            "bash-pwsh-payload-grant-does-not-cover-authored-command",
            Bash("pwsh -NoProfile -Command 'git push'"),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Require(["pwsh"])),
        Case(
            "bash-treats-windows-powershell-as-ordinary-command",
            Bash("powershell.exe -NoProfile -Command 'Get-Content ./a.txt'"),
            Approvals.None,
            ExpectedApproval.Require(["powershell.exe"])),
        Case(
            "powershell7-safe-command-allows",
            PowerShell7("Get-ChildItem -Path . -Filter *.cs"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "powershell7-pipeline-prompts-for-unsafe-stage",
            PowerShell7("Get-ChildItem | Remove-Item"),
            Approvals.None,
            ExpectedApproval.Require(["Remove-Item"])),
        Case(
            "powershell7-stored-grant-covers-unsafe-stage",
            PowerShell7("Get-ChildItem | Remove-Item"),
            Approvals.PersistentAnywhere("Remove-Item"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Remove-Item")),
        Case(
            "powershell7-stop-process-hard-deny",
            PowerShell7("Stop-Process -Id 42"),
            Approvals.PersistentAnywhere("Stop-Process"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "powershell7-elevated-process-hard-deny",
            PowerShell7("Start-Process pwsh -Verb RunAs"),
            Approvals.PersistentAnywhere("Start-Process"),
            ExpectedApproval.Deny("hard_deny_privilege_escalation")),
        Case(
            "powershell7-elevated-process-abbreviated-quoted-hard-deny",
            PowerShell7("Start-Process pwsh -Ve 'RunAs'"),
            Approvals.PersistentAnywhere("Start-Process"),
            ExpectedApproval.Deny("hard_deny_privilege_escalation")),
        Case(
            "powershell7-recursive-root-removal-hard-deny",
            PowerShell7(@"Remove-Item C:\ -Recurse -Confirm:$false"),
            Approvals.PersistentAnywhere("Remove-Item"),
            ExpectedApproval.Deny("hard_deny_system_destructive")),
        Case(
            "powershell7-dynamic-command-fails-closed",
            PowerShell7("& $command"),
            Approvals.PersistentAnywhere("Get-ChildItem"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-treats-bash-payload-as-ordinary-argument",
            PowerShell7("bash -lc 'Remove-Item victim.txt'"),
            Approvals.None,
            ExpectedApproval.Require(["bash"])),
        Case(
            "powershell7-bash-grant-covers-authored-external-command",
            PowerShell7("bash -lc 'Remove-Item victim.txt'"),
            Approvals.PersistentAnywhere("bash"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:bash")),
        Case(
            "powershell7-same-language-child-recurses-to-body",
            PowerShell7("pwsh -NoProfile -Command 'Remove-Item victim.txt'"),
            Approvals.None,
            ExpectedApproval.Require(["Remove-Item"])),
        Case(
            "powershell7-subexpression-standalone-safe-allows",
            PowerShell7("$(Get-Date)"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "powershell7-subexpression-quoted-path-fails-closed",
            PowerShell7("Get-Content \"$(Get-Date)\""),
            Approvals.PersistentAnywhere("Get-Content", "Get-Date"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-subexpression-multiple-nested-fails-closed",
            PowerShell7("Get-Content \"$(Write-Output $(Get-Date))\" \"$(Get-Location)\""),
            Approvals.PersistentAnywhere("Get-Content", "Write-Output", "Get-Date", "Get-Location"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-subexpression-redirect-target-fails-closed",
            PowerShell7("Get-ChildItem > \"$(Write-Output output.txt)\""),
            Approvals.PersistentAnywhere("Get-ChildItem", "Write-Output"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-subexpression-state-propagates",
            PowerShell7(@"Get-Content ""$(Set-Location C:\temp; Get-Location)""; Get-Content .\after.txt"),
            Approvals.PersistentAnywhere("Get-Content", "Set-Location", "Get-Location"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-directory-change-does-not-create-causal-scope",
            PowerShell7(@"Set-Location C:\Temp; Get-Content result.log"),
            Approvals.PersistentAnywhere("Set-Location", "Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-subexpression-call-operator-fails-closed",
            PowerShell7("& $(Write-Output Get-Date)"),
            Approvals.PersistentAnywhere("Write-Output", "Get-Date"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-subexpression-escaped-literal-allows",
            PowerShell7(@"Get-Content "".\`$(Remove-Item victim.txt)"""),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "powershell7-subexpression-malformed-fails-closed",
            PowerShell7("Get-Content \"$(Get-Date\""),
            Approvals.PersistentAnywhere("Get-Content", "Get-Date"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-direct-region-reuses-body-grant",
            PowerShell7(@"& { Remove-Item .\victim.txt }"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "Remove-Item"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Remove-Item")),
        Case(
            "powershell7-callback-region-reuses-host-and-body-grants",
            PowerShell7(@"Get-ChildItem | ForEach-Object { Remove-Item .\victim.txt }"),
            Approvals.PersistentHere(
                ApprovalDirectoryShape.Project,
                "ForEach-Object",
                "Remove-Item"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:ForEach-Object",
                "persistent:Remove-Item")),
        Case(
            "powershell7-callback-region-host-grant-does-not-cover-body",
            PowerShell7(@"Get-ChildItem | ForEach-Object { Remove-Item .\victim.txt }"),
            Approvals.PersistentHere(
                ApprovalDirectoryShape.Project,
                "ForEach-Object"),
            ExpectedApproval.Require(
                ["Remove-Item"],
                approvalMatches: ["persistent:ForEach-Object"])),
        Case(
            "powershell7-callback-region-body-grant-does-not-cover-host",
            PowerShell7(@"Get-ChildItem | ForEach-Object { Remove-Item .\victim.txt }"),
            Approvals.PersistentHere(
                ApprovalDirectoryShape.Project,
                "Remove-Item"),
            ExpectedApproval.Require(
                ["ForEach-Object"],
                approvalMatches: ["persistent:Remove-Item"])),
        Case(
            "powershell7-unknown-region-grants-do-not-cover-incomplete-receiver",
            PowerShell7(@"Invoke-Custom { Remove-Item .\victim.txt }"),
            Approvals.PersistentHere(
                ApprovalDirectoryShape.Project,
                "Invoke-Custom",
                "Remove-Item"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-alias-resolves-before-safe-verb-check",
            PowerShell7("gci"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "powershell7-local-redirect-prompts-for-writer",
            PowerShell7(@"Get-Content .\input.txt > .\output.txt"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Content"])),
        Case(
            "powershell7-protected-path-denies-before-approval",
            PowerShell7(@"Get-Content C:\protected\config\secret.txt"),
            Approvals.PersistentAnywhere("Get-Content"),
            ExpectedApproval.Deny("shell_references_protected_path")),
        Case(
            "powershell7-provider-drive-is-reviewed",
            PowerShell7(@"Get-Content Env:\Path"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-environment-provider-value-stays-strict",
            PowerShell7("Get-Content Env:SECRET"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-unsafe-catalog-gh-web-prompts",
            PowerShell7("gh run view 123456 --web"),
            Approvals.None,
            ExpectedApproval.Require(["gh run view"])),
        Case(
            "powershell7-findstr-external-option-path-prompts",
            PowerShell7(@"findstr /G:C:\outside\patterns.txt C:\project\data.txt"),
            Approvals.None,
            ExpectedApproval.Require(["findstr"])),
        Case(
            "powershell7-output-variable-alone-allows",
            PowerShell7("Get-Date -OutVariable marker"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "powershell7-output-variable-execution-stays-strict",
            PowerShell7("Get-Date -OutVariable marker; & $marker"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-incomplete-pipeline-fails-closed",
            PowerShell7("Get-ChildItem |"),
            Approvals.PersistentAnywhere("Get-ChildItem"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-foreach-inherited-state-prompts",
            PowerShell7("foreach ($f in @('a.txt', 'b.txt')) { Get-Content -LiteralPath $f }"),
            Approvals.PersistentAnywhere("Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-foreach-mutation-inherited-state-prompts",
            PowerShell7("foreach ($f in @('a.txt', 'b.txt')) { Remove-Item -LiteralPath $f }"),
            Approvals.PersistentAnywhere("Remove-Item"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-foreach-dynamic-identity-fails-closed",
            PowerShell7("foreach ($f in @('a.txt', 'b.txt')) { & $command $f }"),
            Approvals.PersistentAnywhere("Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-foreach-child-unknown-state-prompts",
            PowerShell7("pwsh -NoProfile -NonInteractive -Command 'foreach ($f in @(\"a.txt\", \"b.txt\")) { Get-Content -LiteralPath $f }'"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-foreach-child-mutation-prompts",
            PowerShell7("pwsh -NoProfile -NonInteractive -Command 'foreach ($f in @(\"a.txt\", \"b.txt\")) { Remove-Item -LiteralPath $f }'"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-foreach-child-grant-does-not-cover-unknown-state",
            PowerShell7("pwsh -NoProfile -NonInteractive -Command 'foreach ($f in @(\"a.txt\", \"b.txt\")) { Remove-Item -LiteralPath $f }'"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "Remove-Item"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell7-foreach-child-unknown-state-hard-deny",
            PowerShell7("pwsh -NoProfile -NonInteractive -Command 'foreach ($f in @(\"a\", \"b\")) { Stop-Process -Name netclaw }'"),
            Approvals.PersistentAnywhere("Stop-Process"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "powershell51-safe-command-allows",
            WindowsPowerShell51("Get-ChildItem"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "powershell51-directory-change-does-not-create-causal-scope",
            WindowsPowerShell51(@"Set-Location C:\Temp; Get-Content result.log"),
            Approvals.PersistentAnywhere("Set-Location", "Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell51-foreach-inherited-state-prompts",
            WindowsPowerShell51("foreach ($f in @('a.txt', 'b.txt')) { Get-Content -LiteralPath $f }"),
            Approvals.PersistentAnywhere("Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell51-foreach-child-grant-does-not-cover-unknown-state",
            WindowsPowerShell51("powershell.exe -NoProfile -NonInteractive -Command 'foreach ($f in @(\"a.txt\", \"b.txt\")) { Remove-Item -LiteralPath $f }'"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "Remove-Item"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell51-pipeline-chain-fails-closed",
            WindowsPowerShell51("Get-ChildItem && Get-Content .\\a.txt"),
            Approvals.PersistentAnywhere("Get-ChildItem", "Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "powershell51-stop-process-hard-deny",
            WindowsPowerShell51("Stop-Process -Name netclaw"),
            Approvals.PersistentAnywhere("Stop-Process"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "env-nested-shell-prompts",
            Bash("env bash -lc \"git push\""),
            Approvals.None,
            ExpectedApproval.Require(["env bash", "git push"])),
        Case(
            "nohup-nested-shell-prompts",
            Bash("nohup bash -lc \"git push\""),
            Approvals.None,
            ExpectedApproval.Require(["nohup bash", "git push"])),
        Case(
            "timeout-nested-shell-prompts",
            Bash("timeout 5 bash -lc \"git push\""),
            Approvals.None,
            ExpectedApproval.Require(["timeout", "git push"])),
        Case(
            "subshell-prompts",
            Bash("(git status && git push)"),
            Approvals.None,
            ExpectedApproval.Require(["git push"])),
        Case(
            "command-substitution-fails-closed",
            Bash("echo $(git push)"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "bash-substitution-quoted-path-fails-closed",
            Bash("cat \"$(git status)\""),
            Approvals.PersistentAnywhere("cat", "git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "bash-substitution-multiple-nested-fails-closed",
            Bash("cat \"$(printf '%s' \"$(git status)\")\" \"$(dotnet --info)\""),
            Approvals.PersistentAnywhere("cat", "printf", "git status", "dotnet"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "bash-substitution-redirect-target-fails-closed",
            Bash("git status > \"$(printf result.log)\""),
            Approvals.PersistentAnywhere("git status", "printf"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "bash-substitution-state-is-isolated",
            Bash("cat \"$(cd /tmp && pwd)\""),
            Approvals.PersistentAnywhere("cat", "cd", "pwd"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "bash-substitution-escaped-literal-allows",
            Bash("cat \"./\\$(git push)\""),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "bash-substitution-malformed-fails-closed",
            Bash("cat \"$(git status\""),
            Approvals.PersistentAnywhere("cat", "git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "dynamic-path-fails-closed",
            Bash("cat \"$FILE\""),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "dynamic-redirect-fails-closed",
            Bash("git status > \"$OUTPUT\""),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "fd-dup-redirect-safe-verb-allows",
            Bash("git status 2>&1"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "fd-dup-redirect-safe-pipeline-allows",
            Bash("git ls-tree HEAD 2>&1 | tail -20"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "fd-close-redirect-safe-verb-allows",
            Bash("git status 2>&-"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "fd-move-redirect-safe-verb-allows",
            Bash("git status 2>&1-"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "combined-output-project-redirect-safe-verb-prompts",
            Bash("git status &> result.log"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "combined-output-append-project-redirect-safe-verb-prompts",
            Bash("git status &>> result.log"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "numeric-source-project-redirect-safe-verb-prompts",
            Bash("git status 3> result.log"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "fd-dup-redirect-mutating-no-grant-prompts-not-messy",
            Bash("git push origin dev 2>&1 | tail -2"),
            Approvals.None,
            ExpectedApproval.Require(["git push origin dev"], isMessy: false)),
        Case(
            "dynamic-fd-redirect-fails-closed",
            Bash("git status 2>&$FD"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "background-list-prompts-for-mutating-tail",
            Bash("git status & git push"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
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
                ["curl"],
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
            Bash($"cat < {TemporaryFile("netclaw-approval-input.txt")}"),
            Approvals.None,
            ExpectedApproval.Require(["cat"])),
        Case(
            "error-redirect-outside-zone-prompts",
            Bash($"git status 2> {TemporaryFile("netclaw-approval-errors.txt")}"),
            Approvals.None,
            ExpectedApproval.Require(["git status"])),
        Case(
            "cd-current-then-safe-prompts-for-navigation",
            Bash("cd . && git status"),
            Approvals.None,
            ExpectedApproval.Require(["cd"])),
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
            ExpectedApproval.Require(["git push"])),
        Case(
            "literal-heredoc-cat-allows",
            Bash("cat <<'EOF'\nhello\nEOF"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "expanding-heredoc-cat-prompts",
            Bash("cat <<EOF\nhello\nEOF"),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "dynamic-heredoc-cat-prompts",
            Bash("cat <<EOF\n$value\nEOF"),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "literal-here-string-cat-allows",
            Bash("cat <<< \"hello\""),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "dynamic-here-string-cat-prompts",
            Bash("cat <<< \"$value\""),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "here-string-cat-with-argument-prompts",
            Bash("cat -n <<< \"hello\""),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "here-string-interpreter-grant-prompts",
            Bash("bash <<< \"echo ok\""),
            Approvals.PersistentAnywhere("bash"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),

        // These synthetic cases represent the dominant search, pipeline, and
        // file-change shapes in the sanitized local approval-prompt sample.
        // No command text, path, identifier, or free text came from the sample.
        Case(
            "workload-search-rg-in-project-prompts",
            Bash("rg -n \"TODO\" src"),
            Approvals.None,
            ExpectedApproval.Require(["rg"])),
        Case(
            "workload-search-grep-in-project-allows",
            Bash("grep -R \"error\" src"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "workload-search-find-in-project-prompts",
            Bash("find src -name \"*.cs\" -print"),
            Approvals.None,
            ExpectedApproval.Require(["find"])),
        Case(
            "workload-search-cat-in-project-allows",
            Bash("cat src/file.txt"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "workload-search-head-in-project-allows",
            Bash("head -40 src/file.txt"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "workload-search-tail-in-project-allows",
            Bash("tail -100 logs/app.log"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "workload-search-sed-print-in-project-currently-prompts",
            Bash("sed -n '20,80p' src/file.txt"),
            Approvals.None,
            ExpectedApproval.Require(["sed"])),
        Case(
            "workload-search-rg-external-prompts",
            Bash("rg -n \"TODO\" .", ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["rg"])),
        Case(
            "workload-search-rg-external-grant-allows",
            Bash("rg -n \"TODO\" .", ApprovalDirectoryShape.External),
            Approvals.PersistentHere(ApprovalDirectoryShape.External, "rg"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:rg")),
        Case(
            "workload-search-rg-head-pipeline-prompts",
            Bash("rg -n \"TODO\" src | head -40"),
            Approvals.None,
            ExpectedApproval.Require(["rg"])),
        Case(
            "workload-search-grep-tail-pipeline-allows",
            Bash("grep -R \"error\" logs | tail -20"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)),
        Case(
            "workload-search-find-head-pipeline-prompts",
            Bash("find src -name \"*.cs\" -print | head -20"),
            Approvals.None,
            ExpectedApproval.Require(["find"])),
        Case(
            "workload-search-cat-jq-pipeline-prompts-for-tail",
            Bash("cat config.json | jq '.items[]'"),
            Approvals.None,
            ExpectedApproval.Require(["jq"])),
        Case(
            "workload-search-jq-direct-prompts",
            Bash("jq '.items[]' config.json"),
            Approvals.None,
            ExpectedApproval.Require(["jq"])),
        Case(
            "workload-search-jq-direct-grant-allows",
            Bash("jq '.items[]' config.json"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "jq"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:jq")),
        Case(
            "workload-search-cat-jq-stored-tail-allows",
            Bash("cat config.json | jq '.items[]'"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "jq"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:jq")),
        Case(
            "workload-search-cat-jq-external-stored-tail-still-prompts",
            Bash("cat config.json | jq '.items[]'", ApprovalDirectoryShape.External),
            Approvals.PersistentHere(ApprovalDirectoryShape.External, "jq"),
            ExpectedApproval.Require(
                ["cat"],
                approvalMatches: ["persistent:jq"])),
        Case(
            "workload-edit-grep-tee-pipeline-prompts",
            Bash("grep \"error\" logs/app.log | tee reports/errors.txt"),
            Approvals.None,
            ExpectedApproval.Require(["tee"])),
        Case(
            "workload-edit-tee-direct-prompts",
            Bash("tee reports/output.txt"),
            Approvals.None,
            ExpectedApproval.Require(["tee"])),
        Case(
            "workload-edit-tee-direct-grant-allows",
            Bash("tee reports/output.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "tee"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:tee")),
        Case(
            "workload-edit-grep-tee-stored-tail-allows",
            Bash("grep \"error\" logs/app.log | tee reports/errors.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "tee"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:tee")),
        Case(
            "workload-edit-grep-tee-mismatched-tail-grant-prompts",
            Bash("grep \"error\" logs/app.log | tee reports/errors.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.External, "tee"),
            ExpectedApproval.Require(["tee"])),
        Case(
            "workload-edit-sed-in-place-prompts",
            Bash("sed -i 's/old/new/' src/file.txt"),
            Approvals.None,
            ExpectedApproval.Require(["sed"])),
        Case(
            "workload-edit-sed-in-place-grant-allows",
            Bash("sed -i 's/old/new/' src/file.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "sed"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:sed")),
        Case(
            "workload-edit-copy-prompts",
            Bash("cp src/input.txt src/output.txt"),
            Approvals.None,
            ExpectedApproval.Require(["cp"])),
        Case(
            "workload-edit-copy-grant-allows",
            Bash("cp src/input.txt src/output.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "cp"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:cp")),
        Case(
            "workload-edit-move-prompts",
            Bash("mv src/old.txt src/new.txt"),
            Approvals.None,
            ExpectedApproval.Require(["mv"])),
        Case(
            "workload-edit-move-grant-allows",
            Bash("mv src/old.txt src/new.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "mv"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:mv")),
        Case(
            "workload-edit-touch-prompts",
            Bash("touch src/new.txt"),
            Approvals.None,
            ExpectedApproval.Require(["touch"])),
        Case(
            "workload-edit-touch-grant-allows",
            Bash("touch src/new.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:touch")),
        Case(
            "workload-edit-mkdir-prompts",
            Bash("mkdir -p reports/output"),
            Approvals.None,
            ExpectedApproval.Require(["mkdir"])),
        Case(
            "workload-edit-mkdir-grant-allows",
            Bash("mkdir -p reports/output"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "mkdir"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:mkdir")),
        Case(
            "workload-edit-remove-prompts",
            Bash("rm -- src/obsolete.txt"),
            Approvals.None,
            ExpectedApproval.Require(["rm"])),
        Case(
            "workload-edit-remove-grant-allows",
            Bash("rm -- src/obsolete.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "rm"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:rm")),
        Case(
            "workload-edit-printf-redirect-prompts",
            Bash("printf '%s\\n' \"text\" > reports/output.txt"),
            Approvals.None,
            ExpectedApproval.Require(["printf"])),
        Case(
            "workload-edit-printf-redirect-grant-allows",
            Bash("printf '%s\\n' \"text\" > reports/output.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "printf"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:printf")),
        Case(
            "workload-edit-search-pipeline-redirect-in-project-prompts-for-writer",
            Bash("grep -R \"error\" logs | head -20 > reports/errors.txt"),
            Approvals.None,
            ExpectedApproval.Require(["head"])),
        Case(
            "workload-edit-search-pipeline-redirect-external-prompts",
            Bash(
                "grep -R \"error\" logs | head -20 > reports/errors.txt",
                ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["grep", "head"])),
        Case(
            "workload-edit-search-pipeline-redirect-external-grant-allows",
            Bash(
                "grep -R \"error\" logs | head -20 > reports/errors.txt",
                ApprovalDirectoryShape.External),
            Approvals.PersistentHere(ApprovalDirectoryShape.External, "grep", "head"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:grep",
                "persistent:head")),
        Case(
            "workload-search-loop-inherited-state-prompts",
            Bash("for f in src/*.cs; do grep -n \"TODO\" \"$f\"; done"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "grep"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "workload-edit-loop-inherited-state-prompts",
            Bash("for f in src/a.txt src/b.txt; do sed -i 's/old/new/' \"$f\"; done"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "sed"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "workload-search-loop-child-unknown-state-prompts",
            Bash("bash --noprofile --norc -c 'for f in src/a.cs src/b.cs; do grep -n TODO \"$f\"; done'"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "workload-edit-loop-child-grant-does-not-cover-unknown-state",
            Bash("bash --noprofile --norc -c 'for f in src/a.txt src/b.txt; do rm -- \"$f\"; done'"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "rm"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "workload-search-dynamic-root-remains-complex",
            Bash("grep -R \"error\" \"$SEARCH_ROOT\""),
            Approvals.PersistentAnywhere("grep"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "workload-search-substitution-pipeline-redirect-remains-complex",
            Bash("pattern=$(printf '%s' error); grep -R \"$pattern\" src | head -20 > reports/errors.txt"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "grep", "head", "printf"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "workload-search-loop-substitution-pipeline-redirect-remains-complex",
            Bash("for f in logs/*.log; do grep -n \"$(printf '%s' error)\" \"$f\" | head -20 > \"reports/$f.txt\"; done"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "grep", "head", "printf"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),

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
            "echo-control-word-argument-allows",
            Bash("echo done"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ApprovalExemptShellCandidates)),
        Case(
            "control-flow-fails-closed",
            Bash("for f in *.txt; do cat \"$f\"; done"),
            Approvals.PersistentAnywhere("cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "printf-variable-target-hidden-execution-fails-closed",
            Bash("printf -v'value[$(printf marker >&2)0]' '%s' data"),
            Approvals.PersistentAnywhere("printf"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "recursive-builtin-eval-fails-closed",
            Bash("command -p -- builtin -- eval 'printf marker >&2'"),
            Approvals.PersistentAnywhere("command", "builtin", "eval", "printf"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "process-substitution-fails-closed",
            Bash("cat <(git push)"),
            Approvals.PersistentAnywhere("cat", "git push"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "arithmetic-expansion-fails-closed",
            Bash("echo $((1 + 2))"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "function-definition-fails-closed",
            Bash("deploy() { git push; }; deploy"),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "unknown-state-named-parameter-fails-closed",
            Bash("printf '%s' \"$value\""),
            Approvals.PersistentAnywhere("printf"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "nameref-deferred-execution-fails-closed",
            Bash("declare -a values; declare -n current='values[$(printf marker >&2)0]'; " +
                "cat <<EOF\n${current}\nEOF"),
            Approvals.PersistentAnywhere("declare", "printf", "cat"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "source-builtin-payload-fails-closed",
            Bash("source ./bootstrap.sh"),
            Approvals.PersistentAnywhere("source"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "exec-command-resolution-mutation-fails-closed",
            Bash("exec git status"),
            Approvals.PersistentAnywhere("exec", "git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "hash-command-resolution-mutation-fails-closed",
            Bash("hash -p /usr/bin/git git && git status"),
            Approvals.PersistentAnywhere("hash", "git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "alias-command-resolution-mutation-fails-closed",
            Bash("alias inspect='git status'; inspect"),
            Approvals.PersistentAnywhere("alias", "inspect", "git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "shell-option-mutation-fails-closed",
            Bash("shopt -s expand_aliases && git status"),
            Approvals.PersistentAnywhere("shopt", "git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "builtin-enable-mutation-fails-closed",
            Bash("enable -n printf && git status"),
            Approvals.PersistentAnywhere("enable", "git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "time-reserved-form-fails-closed",
            Bash("time git status"),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "negation-reserved-form-fails-closed",
            Bash("! git status"),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "coprocess-reserved-form-fails-closed",
            Bash("coproc git status"),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "brace-group-reserved-form-fails-closed",
            Bash("{ git status; }"),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "inline-python-prompts-for-interpreter",
            Bash("python3 -c \"print('hello')\""),
            Approvals.None,
            ExpectedApproval.Require(["python3"])),
        Case(
            "inline-python-interpreter-grant-currently-allows",
            Bash("python3 -c \"print('hello')\""),
            Approvals.PersistentAnywhere("python3"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:python3")),
        Case(
            "eval-prompts-for-interpreter",
            Bash("eval \"$CODE\""),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "eval-grant-does-not-cover-dynamic-payload",
            Bash("eval \"$CODE\""),
            Approvals.PersistentAnywhere("eval"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "inline-python-heredoc-fails-closed",
            Bash("python3 <<'PY'\nprint('hello')\nPY"),
            Approvals.PersistentAnywhere("python3"),
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
                ["git push"],
                false,
                1,
                "persistent:git status")),
        Case(
            "four-unapproved-clauses-prompt",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.None,
            ExpectedApproval.Require(["git add", "git commit", "git push", "gh pr merge"])),
        Case(
            "four-anywhere-grants-allow",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.PersistentAnywhere("git add", "git commit", "git push", "gh pr merge"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git add",
                "persistent:git commit",
                "persistent:git push",
                "persistent:gh pr merge")),
        Case(
            "four-one-missing-grant-prompts",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.PersistentAnywhere("git add", "git commit", "git push"),
            ExpectedApproval.Require(
                ["gh pr merge"],
                approvalMatches:
                [
                    "persistent:git add",
                    "persistent:git commit",
                    "persistent:git push"
                ])),
        Case(
            "four-here-grants-allow",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.PersistentHere(
                ApprovalDirectoryShape.Project,
                "git add",
                "git commit",
                "git push",
                "gh pr merge"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git add",
                "persistent:git commit",
                "persistent:git push",
                "persistent:gh pr merge")),
        Case(
            "four-one-wrong-directory-grant-prompts",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.Combine(
                Approvals.PersistentHere(
                    ApprovalDirectoryShape.Project,
                    "git add",
                    "git commit",
                    "git push"),
                Approvals.PersistentHere(ApprovalDirectoryShape.External, "gh pr merge")),
            ExpectedApproval.Require(
                ["gh pr merge"],
                approvalMatches:
                [
                    "persistent:git add",
                    "persistent:git commit",
                    "persistent:git push"
                ])),
        Case(
            "four-one-other-session-grant-prompts",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.Combine(
                Approvals.Session("git add", "git commit", "git push"),
                Approvals.SessionForOtherSession("gh pr merge")),
            ExpectedApproval.Require(
                ["gh pr merge"],
                approvalMatches:
                [
                    "session:git add",
                    "session:git commit",
                    "session:git push"
                ])),
        Case(
            "four-one-other-audience-grant-prompts",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.Combine(
                Approvals.PersistentAnywhere("git add", "git commit", "git push"),
                Approvals.PersistentForOtherAudience("gh pr merge")),
            ExpectedApproval.Require(
                ["gh pr merge"],
                approvalMatches:
                [
                    "persistent:git add",
                    "persistent:git commit",
                    "persistent:git push"
                ])),
        Case(
            "four-mixed-grant-sources-allow",
            Bash("git add . && git commit -m fix && git push && gh pr merge 123"),
            Approvals.Combine(
                Approvals.Session("git add", "gh pr merge"),
                Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git commit"),
                Approvals.PersistentAnywhere("git push")),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "session:git add",
                "persistent:git commit",
                "persistent:git push",
                "session:gh pr merge")),
        Case(
            "safe-and-stored-authority-compose",
            Bash("git status && git push && git ls-tree HEAD && gh pr merge 123"),
            Approvals.PersistentAnywhere("git push", "gh pr merge"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git push",
                "persistent:gh pr merge")),
        Case(
            "four-hard-deny-beats-grants",
            Bash("git add . && git commit -m fix && netclaw daemon stop && git push"),
            Approvals.PersistentAnywhere(
                "git add",
                "git commit",
                "netclaw daemon stop",
                "git push"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "four-or-branches-with-grants-allow",
            Bash("git add . || git commit -m fix || git push || gh pr merge 123"),
            Approvals.PersistentAnywhere("git add", "git commit", "git push", "gh pr merge"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git add",
                "persistent:git commit",
                "persistent:git push",
                "persistent:gh pr merge")),
        Case(
            "four-newline-statements-with-grants-allow",
            Bash("git add .\ngit commit -m fix\ngit push\ngh pr merge 123"),
            Approvals.PersistentAnywhere("git add", "git commit", "git push", "gh pr merge"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git add",
                "persistent:git commit",
                "persistent:git push",
                "persistent:gh pr merge")),
        Case(
            "four-subshell-clauses-with-grants-allow",
            Bash("(git add . && git commit -m fix) || (git push && gh pr merge 123)"),
            Approvals.PersistentAnywhere("git add", "git commit", "git push", "gh pr merge"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git add",
                "persistent:git commit",
                "persistent:git push",
                "persistent:gh pr merge")),

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
        CreateRow(testCase));

    public static IEnumerable<TheoryDataRow<string>> BashRows => All
        .Where(testCase => testCase.Invocation.Host == ShellApprovalHost.Bash)
        .Select(CreateRow);

    public static IEnumerable<TheoryDataRow<string>> PowerShellRows => All
        .Where(testCase => testCase.Invocation.Host != ShellApprovalHost.Bash)
        .Select(CreateRow);

    private static TheoryDataRow<string> CreateRow(ShellApprovalCase testCase) =>
        new TheoryDataRow<string>(testCase.Id)
            .WithTestDisplayName($"shell approval :: {testCase.Id}")
            .WithTrait("Disposition", testCase.Expected.Outcome.ToString())
            .WithTrait("AllowReason", testCase.Expected.AllowReason?.ToString() ?? "NotAllowed");

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
            "| ID | Host | Audience | Cwd | Interaction | Command | Approval state | Result | Reason | Candidates | Complex |",
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"
        };

        lines.AddRange(All.Select(testCase =>
            $"| {testCase.Id} | {testCase.Invocation.Host} | {testCase.Invocation.Audience} | {testCase.Invocation.WorkingDirectory} | " +
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

    private static ShellApprovalInvocation PowerShell7(
        string command,
        ApprovalDirectoryShape workingDirectory = ApprovalDirectoryShape.Project,
        TrustAudience audience = TrustAudience.Personal,
        bool interactive = true)
        => new(command, workingDirectory, audience, interactive, ShellApprovalHost.PowerShell7);

    private static ShellApprovalInvocation WindowsPowerShell51(
        string command,
        ApprovalDirectoryShape workingDirectory = ApprovalDirectoryShape.Project,
        TrustAudience audience = TrustAudience.Personal,
        bool interactive = true)
        => new(command, workingDirectory, audience, interactive, ShellApprovalHost.WindowsPowerShell51);

    private static string TemporaryFile(string fileName)
        => $"/netclaw-approval-external/{fileName}";

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
