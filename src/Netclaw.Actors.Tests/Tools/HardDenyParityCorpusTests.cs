// -----------------------------------------------------------------------
// <copyright file="HardDenyParityCorpusTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The hard-deny parity corpus for the shell-facts slice. Each input comes from
/// a catalog Denied row, a hard-deny unit test, an operator override test, or a
/// PR 1b boundary case. Every input goes through the production registration
/// and the tool executor.
/// </summary>
/// <remarks>
/// ShellSyntaxTree is the only reader of shell syntax. A denied input stays
/// denied with the same reason. An input that the parser cannot resolve can
/// instead get an exact one-time prompt without candidates. It is denied when
/// no operator can answer, and it never becomes allowed or reusable.
/// </remarks>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class HardDenyParityCorpusTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SelfDestructive = "hard_deny_self_destructive";
    private const string PrivilegeEscalation = "hard_deny_privilege_escalation";
    private const string SystemDestructive = "hard_deny_system_destructive";
    private const string CustomDeny = "hard_deny_custom_deny";
    private const string ProtectedPath = "shell_references_protected_path";

    private static readonly ShellApprovalHarnessPolicy BashOverrides = new()
    {
        HardDenyPatterns = ["docker rm", "legacy-bad-tool", "terraform destroy"],
        HardDenyOverridesJson = """
            [
              { "verb": ["docker", "rm"], "reason": "local_policy" },
              { "verbPrefix": "danger.", "reason": "test_family" },
              { "rawText": "MAGIC_DENY_TOKEN", "reason": "raw_token" },
              { "verb": ["tar"], "argFlags": ["--delete"], "reason": "tar_delete" },
              { "verb": ["custom-tool"], "argFlags": ["-rf"], "reason": "custom_flags" },
              { "verb": ["path-tool"], "firstPath": { "oneOf": ["/", "/etc"] }, "reason": "custom_path" },
              { "verb": ["modern", "bad"], "reason": "modern_policy" }
            ]
            """
    };

    private static readonly ShellApprovalHarnessPolicy PowerShellLegacySpelling = new()
    {
        HardDenyPatterns = ["custom-tool $operation"]
    };

    private static readonly ShellApprovalHarnessPolicy PowerShellOverrides = new()
    {
        HardDenyPatterns = ["custom-tool delete"],
        HardDenyOverridesJson = """
            [
              { "verb": ["path-tool"], "argFlags": ["--delete"], "firstPath": { "oneOf": ["~"] }, "reason": "custom_path" },
              { "verb": ["root-tool"], "firstPath": { "oneOf": ["/"] }, "reason": "custom_root" }
            ]
            """
    };

    internal enum Expectation
    {
        /// <summary>Denied with the named reason, interactive and unattended.</summary>
        Denied,

        /// <summary>The parser cannot resolve the input: denied, or an exact one-time prompt.</summary>
        DeniedOrExactConsent,

        /// <summary>A negative control: the hard-deny list does not apply.</summary>
        NotDenied,
    }

    internal sealed record CorpusRow(
        string Id,
        string Source,
        ShellApprovalHost Host,
        string Command,
        Expectation Expected,
        string? Reason = null,
        ShellApprovalHarnessPolicy? Policy = null,
        TrustAudience Audience = TrustAudience.Personal)
    {
        /// <summary>
        /// The denial reason on a Windows host, when the base result depends on the host.
        /// </summary>
        public string? WindowsHostDenyReason { get; init; }
    }

    // On a Windows host, a drive root in PowerShell text (C:\, or / as the current
    // drive root) is a protected-path hit in ToolPathPolicy and FileSystemAuthority.
    // The base gives the same result there. The hard-deny list does not apply.
    private const string WindowsDriveRootReason = ProtectedPath;

    private static CorpusRow Deny(string id, string source, string command, string reason, ShellApprovalHarnessPolicy? policy = null)
        => new(id, source, ShellApprovalHost.Bash, command, Expectation.Denied, reason, policy);

    private static CorpusRow Unresolved(string id, string source, string command, string reason)
        => new(id, source, ShellApprovalHost.Bash, command, Expectation.DeniedOrExactConsent, reason);

    private static CorpusRow Control(string id, string source, string command, ShellApprovalHarnessPolicy? policy = null)
        => new(id, source, ShellApprovalHost.Bash, command, Expectation.NotDenied, Policy: policy);

    private static CorpusRow Pwsh(string id, string source, string command, Expectation expected, string? reason = null, ShellApprovalHarnessPolicy? policy = null, ShellApprovalHost host = ShellApprovalHost.PowerShell7)
        => new(id, source, host, command, expected, reason, policy);

    private const string PolicyTests = "ShellCommandPolicyTests";
    private const string DenyOnlyTests = "ShellCommandDenyOnlyPolicyTests";
    private const string OverrideTests = "ShellCommandPolicyOverrideTests";
    private const string WindowsDenyOnlyTests = "WindowsPowerShellDenyOnlyIntegrationTests";
    private const string WindowsApprovalTests = "WindowsPowerShellDenyOnlyApprovalTests";
    private const string BoundaryTests = "ApprovalContractBoundaryTests";

    internal static IReadOnlyList<CorpusRow> Rows { get; } =
    [
        // ShellCommandPolicyTests: self-destruction, escalation, destruction.
        Deny("policy-daemon-stop", PolicyTests, "netclaw daemon stop", SelfDestructive),
        Deny("policy-daemon-kill", PolicyTests, "netclaw daemon kill", SelfDestructive),
        Deny("policy-systemctl-stop", PolicyTests, "systemctl stop netclaw", SelfDestructive),
        Deny("policy-kill", PolicyTests, "kill -9 $(cat ~/.netclaw/daemon.pid)", SelfDestructive),
        Deny("policy-killall", PolicyTests, "killall netclaw", SelfDestructive),
        Deny("policy-pkill", PolicyTests, "pkill -f netclaw", SelfDestructive),
        Deny("policy-sudo", PolicyTests, "sudo rm -rf /tmp/build", PrivilegeEscalation),
        Deny("policy-su", PolicyTests, "su -c 'whoami'", PrivilegeEscalation),
        Deny("policy-doas", PolicyTests, "doas apt install curl", PrivilegeEscalation),
        Deny("policy-compound-sudo", PolicyTests, "echo hello && sudo kill -9 123", PrivilegeEscalation),
        Deny("policy-upper-sudo", PolicyTests, "SUDO rm -rf /tmp", PrivilegeEscalation),
        Deny("policy-bash-c-sudo", PolicyTests, "bash -c \"sudo kill -9 123\"", PrivilegeEscalation),
        Deny("policy-sudo-bash-lc", PolicyTests, "sudo bash -lc \"git status\"", PrivilegeEscalation),
        Deny("policy-sudo-bin-bash-lc", PolicyTests, "sudo /bin/bash -lc \"git status\"", PrivilegeEscalation),
        Deny("policy-rm-root", PolicyTests, "rm -rf /", SystemDestructive),
        Deny("policy-rm-home", PolicyTests, "rm -rf ~", SystemDestructive),
        Unresolved("policy-rm-home-variable", PolicyTests, "rm -rf $HOME", SystemDestructive),
        Deny("policy-mkfs", PolicyTests, "mkfs.ext4 /dev/sda1", SystemDestructive),
        Unresolved("policy-fork-bomb", PolicyTests, ":(){ :|:& };:", SystemDestructive),
        Deny("policy-compound-daemon", PolicyTests, "echo hello && netclaw daemon stop", SelfDestructive),
        Deny("policy-pipe-daemon", PolicyTests, "echo safe | netclaw daemon stop", SelfDestructive),
        Deny("policy-pipe-sudo", PolicyTests, "printf safe | sudo kill -9 123", PrivilegeEscalation),
        Deny("policy-bash-c-pipe", PolicyTests, "bash -c \"echo safe | netclaw daemon stop\"", SelfDestructive),
        Deny("policy-bash-c", PolicyTests, "bash -c \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-bash-lc", PolicyTests, "bash -lc \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-bash-noprofile-lc", PolicyTests, "bash --noprofile -lc \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-env-bash-lc", PolicyTests, "env bash -lc \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-command-bash-lc", PolicyTests, "command bash -lc \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-timeout-bash-lc", PolicyTests, "timeout 5 bash -lc \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-nice-bash-lc", PolicyTests, "nice -n 5 bash -lc \"netclaw daemon stop\"", SelfDestructive),
        Unresolved("policy-decoded-and-bundled-wrapper", PolicyTests, "bash -c \"echo safe\" && bash -lc \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-dash-c", PolicyTests, "dash -c \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-bin-dash-c", PolicyTests, "/bin/dash -c \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-zsh-c", PolicyTests, "/usr/bin/zsh -c \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-ksh-c", PolicyTests, "ksh -c \"netclaw daemon stop\"", SelfDestructive),
        Deny("policy-case-daemon", PolicyTests, "Netclaw Daemon Stop", SelfDestructive),
        Deny("policy-case-kill", PolicyTests, "KILL -9 $(PGREP NETCLAWD)", SelfDestructive),
        Deny("policy-split-flags", PolicyTests, "rm -r -f /", SystemDestructive),
        Deny("policy-long-flags", PolicyTests, "rm --recursive --force /", SystemDestructive),
        Deny("policy-custom-pattern", PolicyTests, "docker rm my-container", CustomDeny, BashOverrides),
        Control("policy-rm-specific-directory", PolicyTests, "rm -rf /tmp/build-output"),
        Control("policy-git-push", PolicyTests, "git push origin main"),
        Control("policy-ls", PolicyTests, "ls -la /tmp"),
        Control("policy-dotnet-build", PolicyTests, "dotnet build"),
        Control("policy-safe-compound", PolicyTests, "git add . && git commit -m fix && git push"),
        Control("policy-bash-c-safe", PolicyTests, "bash -c \"git status\""),
        Control("policy-bash-does-not-read-pwsh-child", PolicyTests, "pwsh -NoProfile -NonInteractive -Command 'netclaw daemon stop'"),
        Control("policy-custom-pattern-unrelated", PolicyTests, "docker build -t myapp .", BashOverrides),

        // ShellCommandPolicyTests: native PowerShell.
        Pwsh("policy-pwsh-child", PolicyTests, "pwsh -NoProfile -Command 'netclaw daemon stop'", Expectation.Denied, SelfDestructive),
        Pwsh("policy-pwsh-unknown-region", PolicyTests, "Invoke-Custom { netclaw daemon stop }", Expectation.Denied, SelfDestructive),
        Pwsh("policy-pwsh-foreach-mutation", PolicyTests, "Get-ChildItem | ForEach-Object { netclaw daemon stop; $item++ }", Expectation.DeniedOrExactConsent, SelfDestructive),
        Pwsh("policy-pwsh-custom-mutation", PolicyTests, "Invoke-Custom { netclaw daemon stop; $item++ }", Expectation.DeniedOrExactConsent, SelfDestructive),
        Pwsh("policy-pwsh-runas", PolicyTests, "Start-Process pwsh -Verb RunAs", Expectation.Denied, PrivilegeEscalation),
        Pwsh("policy-pwsh-runas-short", PolicyTests, "Start-Process pwsh -Ve RunAs", Expectation.Denied, PrivilegeEscalation),
        // Through the gate the harness project path leaves this input unresolved.
        Pwsh("policy-pwsh-runas-quoted", PolicyTests, "Start-Process pwsh -V 'RunAs'", Expectation.DeniedOrExactConsent, PrivilegeEscalation),
        Pwsh("policy-pwsh-runas-inline", PolicyTests, "Start-Process pwsh -Verb:\"RunAs\"", Expectation.Denied, PrivilegeEscalation),
        Pwsh("policy-pwsh-runas-escape", PolicyTests, "Start-Process pwsh -Verb R`unAs", Expectation.Denied, PrivilegeEscalation),
        Pwsh("policy-pwsh-runas-inline-escape", PolicyTests, "Start-Process pwsh -Verb:R`unAs", Expectation.Denied, PrivilegeEscalation),
        Pwsh("policy-pwsh-runas-child", PolicyTests, "powershell.exe -Command 'Start-Process pwsh -Verb R`unAs'", Expectation.Denied, PrivilegeEscalation),
        Pwsh("policy-pwsh-runas-alias", PolicyTests, "saps pwsh -Verb RunAs", Expectation.Denied, PrivilegeEscalation),
        Pwsh("policy-pwsh-quoted-false", PolicyTests, @"Remove-Item C:\ -Recurse:'$false' -Force", Expectation.Denied, SystemDestructive),
        Pwsh("policy-pwsh-escaped-false", PolicyTests, @"Remove-Item C:\ -Recurse:`$false -Force", Expectation.Denied, SystemDestructive),
        Pwsh("policy-pwsh-recurse", PolicyTests, @"Remove-Item C:\ -Recurse", Expectation.Denied, SystemDestructive),
        Pwsh("policy-pwsh-recurse-short", PolicyTests, @"Remove-Item 'C:\' -Re", Expectation.Denied, SystemDestructive),
        Pwsh("policy-pwsh-literal-provider", PolicyTests, @"Remove-Item -LiteralPath FileSystem::C:\ -R -Confirm:$false", Expectation.Denied, SystemDestructive),
        Pwsh("policy-pwsh-inline-path", PolicyTests, @"Remove-Item -Path:C:\ -Recurse", Expectation.Denied, SystemDestructive),
        Pwsh("policy-pwsh-ri", PolicyTests, @"ri C:\ -Recurse", Expectation.Denied, SystemDestructive),
        Pwsh("policy-pwsh-verbose-control", PolicyTests, "Start-Process pwsh -Verbose RunAs", Expectation.NotDenied),
        Pwsh("policy-pwsh-force-control", PolicyTests, @"Remove-Item C:\ -Force", Expectation.NotDenied) with { WindowsHostDenyReason = WindowsDriveRootReason },
        Pwsh("policy-pwsh-false-control", PolicyTests, @"Remove-Item C:\ -Recurse:$false -Force", Expectation.NotDenied) with { WindowsHostDenyReason = WindowsDriveRootReason },

        // ShellCommandDenyOnlyPolicyTests, as whole commands.
        Pwsh("deny-only-daemon", DenyOnlyTests, "netclaw daemon stop", Expectation.Denied, SelfDestructive),
        Pwsh("deny-only-process-id", DenyOnlyTests, "Stop-Process -Id $processId", Expectation.DeniedOrExactConsent, SelfDestructive),
        Pwsh("deny-only-escaped-head", DenyOnlyTests, "net`claw daemon stop", Expectation.Denied, SelfDestructive),
        Pwsh("deny-only-call-operator", DenyOnlyTests, "& 'netclaw' daemon stop", Expectation.Denied, SelfDestructive),
        Pwsh("deny-only-wrapper", DenyOnlyTests, "pwsh -Command 'netclaw daemon stop'", Expectation.Denied, SelfDestructive),
        Pwsh("deny-only-escaped-runas", DenyOnlyTests, "Start-Process pwsh -Verb R`unAs", Expectation.Denied, PrivilegeEscalation),
        Pwsh("deny-only-quoted-false", DenyOnlyTests, "Remove-Item -Recurse:'$false' -Force /", Expectation.Denied, SystemDestructive),
        Pwsh("deny-only-escaped-false", DenyOnlyTests, "Remove-Item -Recurse:`$false -Force /", Expectation.Denied, SystemDestructive),
        Pwsh("deny-only-custom-static", DenyOnlyTests, "custom-tool delete $target", Expectation.DeniedOrExactConsent, CustomDeny, PowerShellOverrides),
        Pwsh("deny-only-custom-legacy-spelling", DenyOnlyTests, "custom-tool $operation; $item++", Expectation.DeniedOrExactConsent, CustomDeny, PowerShellLegacySpelling),
        Pwsh("deny-only-refined-tilde", DenyOnlyTests, "path-tool --delete ~", Expectation.Denied, CustomDeny, PowerShellOverrides),
        Pwsh("deny-only-dynamic-verb-control", DenyOnlyTests, "Start-Process pwsh -Verb $verb", Expectation.NotDenied),
        Pwsh("deny-only-dynamic-part-control", DenyOnlyTests, "Start-Process pwsh -Verb \"Run$part\"", Expectation.NotDenied),
        Pwsh("deny-only-custom-order-control", DenyOnlyTests, "custom-tool $operation delete", Expectation.NotDenied, policy: PowerShellOverrides),
        Pwsh("deny-only-custom-spelling-control", DenyOnlyTests, "custom-tool $otherOperation; $item++", Expectation.NotDenied, policy: PowerShellLegacySpelling),
        Pwsh("deny-only-refined-dynamic-control", DenyOnlyTests, "path-tool --delete $path ~", Expectation.NotDenied, policy: PowerShellOverrides),
        Pwsh("deny-only-dynamic-root-control", DenyOnlyTests, "root-tool $path /", Expectation.NotDenied, policy: PowerShellOverrides) with { WindowsHostDenyReason = WindowsDriveRootReason },
        Pwsh("deny-only-interleaved-control", DenyOnlyTests, "netclaw --verbose daemon stop", Expectation.NotDenied),
        Pwsh("deny-only-single-quoted-data-control", DenyOnlyTests, "Write-Output 'netclaw daemon stop'", Expectation.NotDenied),
        Pwsh("deny-only-double-quoted-data-control", DenyOnlyTests, "Write-Output \"netclaw daemon stop\"", Expectation.NotDenied),
        Pwsh("deny-only-comment-control", DenyOnlyTests, "Write-Output ok # netclaw daemon stop", Expectation.NotDenied),

        // WindowsPowerShellDenyOnlyIntegrationTests and WindowsPowerShellDenyOnlyApprovalTests.
        Pwsh("windows-foreach-first", WindowsDenyOnlyTests, "Get-ChildItem | ForEach-Object { netclaw daemon stop; $item++ }", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-foreach-second", WindowsDenyOnlyTests, "Get-ChildItem | ForEach-Object { $item++; netclaw daemon stop }", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-custom-first", WindowsDenyOnlyTests, "Invoke-Custom { netclaw daemon stop; $item++ }", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-custom-second", WindowsDenyOnlyTests, "Invoke-Custom { $item++; netclaw daemon stop }", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-remove-true", WindowsDenyOnlyTests, @"Write-Output input | ForEach-Object { Remove-Item -Path C:\ -Recurse:$true; $item++ }", Expectation.DeniedOrExactConsent, SystemDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-remove-flag", WindowsDenyOnlyTests, @"Write-Output input | ForEach-Object { Remove-Item -Path C:\ -Recurse:$flag; $item++ }", Expectation.DeniedOrExactConsent, SystemDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-escaped-head", WindowsDenyOnlyTests, "net`claw daemon stop; $item++", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-call-operator", WindowsDenyOnlyTests, "& 'netclaw' daemon stop; $item++", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-wrapper", WindowsDenyOnlyTests, "powershell.exe -Command 'netclaw daemon stop; $item++'", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-approval-first", WindowsApprovalTests, "netclaw daemon stop; $item++", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-approval-second", WindowsApprovalTests, "$item++; netclaw daemon stop", Expectation.DeniedOrExactConsent, SelfDestructive, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-dynamic-verb-control", WindowsDenyOnlyTests, "Start-Process pwsh -Verb $verb; $item++", Expectation.NotDenied, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-dynamic-part-control", WindowsDenyOnlyTests, "Start-Process pwsh -Verb \"Run$part\"; $item++", Expectation.NotDenied, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-single-quoted-data-control", WindowsDenyOnlyTests, "Write-Output 'netclaw daemon stop'; $item++", Expectation.NotDenied, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-double-quoted-data-control", WindowsDenyOnlyTests, "Write-Output \"netclaw daemon stop\"; $item++", Expectation.NotDenied, host: ShellApprovalHost.WindowsPowerShell51),
        Pwsh("windows-comment-control", WindowsDenyOnlyTests, "Write-Output ok; $item++; # netclaw daemon stop", Expectation.NotDenied, host: ShellApprovalHost.WindowsPowerShell51),

        // ShellCommandPolicyOverrideTests and the PR 1b operator cases.
        Deny("override-verb", OverrideTests, "docker rm my-container", CustomDeny, BashOverrides),
        Deny("override-prefix-ext4", OverrideTests, "danger.ext4 /dev/sda", CustomDeny, BashOverrides),
        Deny("override-prefix-xfs", OverrideTests, "danger.xfs /dev/sda", CustomDeny, BashOverrides),
        Deny("override-raw", OverrideTests, "echo MAGIC_DENY_TOKEN", CustomDeny, BashOverrides),
        Deny("override-flag", OverrideTests, "tar --delete -f archive.tar foo", CustomDeny, BashOverrides),
        Deny("override-combined-flags", OverrideTests, "custom-tool -rfv /tmp", CustomDeny, BashOverrides),
        Deny("override-exact-flags", OverrideTests, "custom-tool -rf /tmp", CustomDeny, BashOverrides),
        Deny("override-first-path-root", OverrideTests, "path-tool /", CustomDeny, BashOverrides),
        Deny("override-first-path-etc", OverrideTests, "path-tool /etc", CustomDeny, BashOverrides),
        Deny("override-defaults-daemon", OverrideTests, "netclaw daemon stop", SelfDestructive, BashOverrides),
        Deny("override-defaults-rm", OverrideTests, "rm -rf /", SystemDestructive, BashOverrides),
        Deny("override-legacy-pattern", OverrideTests, "legacy-bad-tool args", CustomDeny, BashOverrides),
        Deny("override-modern-rule", OverrideTests, "modern bad command", CustomDeny, BashOverrides),
        Deny("boundary-configured-pattern", BoundaryTests, "terraform destroy workspace", CustomDeny, BashOverrides),
        Deny("boundary-configured-pattern-compound", BoundaryTests, "git status && terraform destroy", CustomDeny, BashOverrides),
        Control("override-verb-control-ps", OverrideTests, "docker ps", BashOverrides),
        Control("override-verb-control-run", OverrideTests, "docker run nginx", BashOverrides),
        Control("override-prefix-control", OverrideTests, "safer something", BashOverrides),
        Control("override-raw-control", OverrideTests, "echo hello", BashOverrides),
        Control("override-flag-control", OverrideTests, "tar -czf out.tar foo", BashOverrides),
        Control("override-combined-flags-control", OverrideTests, "custom-tool -v /tmp", BashOverrides),
        Control("override-first-path-control", OverrideTests, "path-tool /tmp", BashOverrides),
        Control("override-safe-command-control", OverrideTests, "safe command", BashOverrides),
        Control("boundary-configured-pattern-prefix-control", BoundaryTests, "terraform destroyer", BashOverrides),
        Control("boundary-configured-pattern-data-control", BoundaryTests, "echo terraform destroy", BashOverrides),

        // Owner decision (2026-09-30): input that the parser cannot read keeps the
        // raw-text denial of the base, also where the shell cannot run the text as written.
        Deny("kept-bash-powershell-remove", "Deferred decision", @"Remove-Item C:\ -Recurse -Confirm:$false", SystemDestructive),
        Pwsh("kept-pwsh-leading-separator", "Deferred decision", "; Stop-Process -Name netclaw", Expectation.Denied, SelfDestructive),
        Pwsh("kept-pwsh-quoted-head-background", "Deferred decision", "'netclaw' daemon stop &", Expectation.Denied, SelfDestructive),
        Pwsh("kept-pwsh51-chain-operator", "Deferred decision", "file_read && rm -rf /", Expectation.Denied, SystemDestructive, host: ShellApprovalHost.WindowsPowerShell51),

        // PR 1b: unresolved input still meets hard deny and protected paths.
        Deny("boundary-background-daemon", BoundaryTests, "netclaw daemon stop &", SelfDestructive),
        Deny("boundary-background-quoted-head", BoundaryTests, "'netclaw' daemon stop &", SelfDestructive),
        Deny("boundary-background-wrapper", BoundaryTests, "bash -c \"pkill netclawd\" &", SelfDestructive),
        Deny("boundary-background-sudo", BoundaryTests, "sudo rm -rf / &", PrivilegeEscalation),
        Deny("boundary-background-protected", BoundaryTests, "cat ../netclaw/config/notes.txt &", ProtectedPath),
    ];

    public static IEnumerable<TheoryDataRow<string>> BashRows
        => Rows.Where(static row => row.Host is ShellApprovalHost.Bash or ShellApprovalHost.Bash52)
            .Select(static row => new TheoryDataRow<string>(row.Id));

    public static IEnumerable<TheoryDataRow<string>> PowerShellRows
        => Rows.Where(static row => row.Host is ShellApprovalHost.PowerShell7 or ShellApprovalHost.WindowsPowerShell51)
            .Select(static row => new TheoryDataRow<string>(row.Id));

    // Content denials only. A trusted-root denial depends on the working directory
    // of its row, which this corpus replaces with the project directory. The
    // unresolved-input denial depends on the run mode of its row.
    public static IEnumerable<TheoryDataRow<string>> CatalogDeniedRows
        => ShellApprovalCases.All
            .Where(static testCase => testCase.Expected.Outcome == ApprovalOutcome.Denied)
            .Where(static testCase => testCase.Expected.DenyReason
                is not ("shell_working_directory_outside_trust_zone"
                    or "shell_path_outside_trust_zone"
                    or "shell_unresolved_trust_zone_input"))
            .Where(static testCase => IsPosix || testCase.Invocation.Host is not (ShellApprovalHost.Bash or ShellApprovalHost.Bash52))
            .Select(static testCase => new TheoryDataRow<string>(testCase.Id));

    [SlopwatchSuppress("SW001", "The Bash corpus rows require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash corpus rows require a POSIX host.")]
    [MemberData(nameof(BashRows))]
    public Task Bash_corpus_input_keeps_its_hard_deny(string id)
        => AssertRowAsync(Rows.Single(row => row.Id == id));

    [Theory]
    [MemberData(nameof(PowerShellRows))]
    public Task Power_shell_corpus_input_keeps_its_hard_deny(string id)
        => AssertRowAsync(Rows.Single(row => row.Id == id));

    [Theory]
    [MemberData(nameof(CatalogDeniedRows))]
    public async Task Catalog_denied_row_stays_denied_unattended(string caseId)
    {
        var testCase = ShellApprovalCases.Get(caseId);
        var row = new CorpusRow(
            caseId,
            "ShellApprovalCaseCatalog",
            testCase.Invocation.Host,
            testCase.Invocation.Command,
            Expectation.Denied,
            testCase.Expected.DenyReason,
            Audience: testCase.Invocation.Audience);

        await AssertRowAsync(row);
    }

    // Owner decision (2026-09-30): each element of a Bash background list
    // meets the hard-deny list. The base scan checked only the first command.
    [SlopwatchSuppress("SW001", "The Bash background cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash background cases require a POSIX host.")]
    [InlineData("echo ok & sudo ls", PrivilegeEscalation)]
    [InlineData("ls & netclaw daemon stop", SelfDestructive)]
    [InlineData("& 'netclaw' daemon stop", SelfDestructive)]
    [InlineData("& 'netclaw' daemon stop; $item++", SelfDestructive)]
    public Task Every_background_list_element_meets_hard_deny(string command, string reason)
        => AssertRowAsync(new CorpusRow(
            $"background-element-{command.Length}",
            "Owner decision",
            ShellApprovalHost.Bash,
            command,
            Expectation.Denied,
            reason));

    private async Task AssertRowAsync(CorpusRow row)
    {
        var interactive = await EvaluateAsync(row, interactive: true);
        var unattended = await EvaluateAsync(row, interactive: false);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"corpus | {row.Id} | {row.Host} | interactive={Describe(interactive)} | unattended={Describe(unattended)}");

        if (OperatingSystem.IsWindows() && row.WindowsHostDenyReason is { } windowsReason)
        {
            Assert.Equal(ApprovalOutcome.Denied, interactive.Outcome);
            Assert.Equal(windowsReason, interactive.DenyReason);
            Assert.Equal(ApprovalOutcome.Denied, unattended.Outcome);
            Assert.Equal(windowsReason, unattended.DenyReason);
            return;
        }

        switch (row.Expected)
        {
            case Expectation.Denied:
                Assert.Equal(ApprovalOutcome.Denied, interactive.Outcome);
                Assert.Equal(row.Reason, interactive.DenyReason);
                Assert.Equal(ApprovalOutcome.Denied, unattended.Outcome);
                Assert.Equal(row.Reason, unattended.DenyReason);
                break;
            case Expectation.DeniedOrExactConsent:
                if (interactive.Outcome == ApprovalOutcome.Denied)
                {
                    Assert.Equal(row.Reason, interactive.DenyReason);
                }
                else
                {
                    AssertExactConsentOnly(interactive);
                }

                Assert.Equal(ApprovalOutcome.Denied, unattended.Outcome);
                break;
            case Expectation.NotDenied:
                Assert.NotEqual(ApprovalOutcome.Denied, interactive.Outcome);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row.Expected, "Unknown corpus expectation.");
        }
    }

    // Unparseable input can ask for one exact retry. It never offers a reusable grant.
    private static void AssertExactConsentOnly(ApprovalObservation observation)
    {
        Assert.Equal(ApprovalOutcome.RequiresApproval, observation.Outcome);
        var prompt = Assert.IsType<ApprovalPromptObservation>(observation.Prompt);
        Assert.True(prompt.IsMessy);
        Assert.Empty(prompt.CandidateVerbs);
        Assert.All(
            prompt.OptionKeys,
            key => Assert.Contains(key, new[] { ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny }));
    }

    private async Task<ApprovalObservation> EvaluateAsync(CorpusRow row, bool interactive)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            $"corpus-{row.Id}-{(interactive ? "interactive" : "unattended")}",
            new ShellApprovalInvocation(row.Command, Audience: row.Audience, Interactive: interactive, Host: row.Host),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            policy: row.Policy);
        return await harness.EvaluateAsync(Ct);
    }

    private static string Describe(ApprovalObservation observation)
        => observation.Outcome switch
        {
            ApprovalOutcome.Denied => $"Denied:{observation.DenyReason}",
            ApprovalOutcome.RequiresApproval => $"RequiresApproval:messy={observation.Prompt?.IsMessy}:options={string.Join(",", observation.Prompt?.OptionKeys ?? [])}",
            _ => observation.Outcome.ToString()
        };
}
