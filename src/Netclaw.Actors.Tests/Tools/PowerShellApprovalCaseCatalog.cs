// -----------------------------------------------------------------------
// <copyright file="PowerShellApprovalCaseCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;
using System.Text;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public static class PowerShellApprovalCases
{
    internal static IReadOnlyList<ShellApprovalCase> All { get; } =
    [
        Case(
            "mutating-command-prompts",
            PowerShell("Set-Content -Path notes.txt -Value hello"),
            Approvals.None,
            ExpectedApproval.Require(["Set-Content"])),
        Case(
            "team-audience-denied",
            PowerShell("Set-Content notes.txt hello", audience: TrustAudience.Team),
            Approvals.None,
            ExpectedApproval.Deny("tool_not_allowed_for_audience_profile")),
        Case(
            "public-audience-denied",
            PowerShell("Set-Content notes.txt hello", audience: TrustAudience.Public),
            Approvals.None,
            ExpectedApproval.Deny("tool_not_allowed_for_audience_profile")),

        Case(
            "hard-deny-blocks",
            PowerShell("Stop-Process -Id 1"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-alias-blocks",
            PowerShell("spps -Id 1"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-beats-stored-grant",
            PowerShell("Stop-Process -Id 1"),
            Approvals.PersistentAnywhere("Stop-Process"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "hard-deny-pipeline-tail-blocks",
            PowerShell("Get-Date | Stop-Process -Id 1"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),
        Case(
            "unsupported-cmd-wrapper-blocks",
            PowerShell("cmd.exe /c whoami"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_custom_deny")),
        Case(
            "unsupported-windows-powershell-wrapper-blocks",
            PowerShell("powershell.exe -Command Get-Date"),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_custom_deny")),
        Case(
            "encoded-hard-deny-blocks",
            PowerShell(EncodedCommand("spps -Id 1")),
            Approvals.None,
            ExpectedApproval.Deny("hard_deny_self_destructive")),

        Case(
            "safe-cmdlet-project-allows",
            PowerShell("Get-ChildItem"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-alias-project-allows",
            PowerShell("gci"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-cmdlet-session-allows",
            PowerShell("Get-ChildItem", ApprovalDirectoryShape.Session),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-cmdlet-external-prompts",
            PowerShell("Get-ChildItem", ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["Get-ChildItem"])),
        Case(
            "safe-project-path-allows",
            PowerShell(@"Get-Content .\notes.txt"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-external-path-prompts",
            PowerShell(@"Get-Content C:\Windows\win.ini"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Content"])),
        Case(
            "environment-provider-prompts",
            PowerShell("Get-ChildItem Env:"),
            Approvals.None,
            ExpectedApproval.Require(["Get-ChildItem"])),
        Case(
            "registry-provider-prompts",
            PowerShell(@"Get-ChildItem Registry::HKEY_LOCAL_MACHINE\SOFTWARE"),
            Approvals.None,
            ExpectedApproval.Require(["Get-ChildItem"])),
        Case(
            "provider-grant-allows",
            PowerShell("Get-ChildItem Env:"),
            Approvals.PersistentAnywhere("Get-ChildItem"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Get-ChildItem")),

        Case(
            "safe-pipeline-allows",
            PowerShell("Get-ChildItem | Select-Object -First 5"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "four-safe-pipeline-clauses-allow",
            PowerShell("Get-ChildItem | Select-Object -First 5 | Sort-Object Name | Format-Table Name"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-pipeline-mutating-tail-prompts",
            PowerShell("Get-ChildItem | Set-Content result.txt"),
            Approvals.None,
            ExpectedApproval.Require(["Get-ChildItem", "Set-Content"])),
        Case(
            "semicolon-sequence-prompts",
            PowerShell("Get-Date; Set-Content notes.txt hello"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Date", "Set-Content"])),
        Case(
            "newline-sequence-prompts",
            PowerShell("Get-Date\nSet-Content notes.txt hello"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Date", "Set-Content"])),
        Case(
            "where-object-remains-gated",
            PowerShell("Get-ChildItem | Where-Object Name -Like *.txt"),
            Approvals.None,
            ExpectedApproval.Require(["Get-ChildItem", "Where-Object"])),

        Case(
            "mutating-alias-prompts-for-canonical-cmdlet",
            PowerShell("ri notes.txt"),
            Approvals.None,
            ExpectedApproval.Require(["Remove-Item"])),
        Case(
            "canonical-grant-allows-alias",
            PowerShell("ri notes.txt"),
            Approvals.PersistentAnywhere("Remove-Item"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Remove-Item")),
        Case(
            "alias-grant-does-not-match-canonical-cmdlet",
            PowerShell("ri notes.txt"),
            Approvals.PersistentAnywhere("ri"),
            ExpectedApproval.Require(["Remove-Item"])),
        Case(
            "stored-grant-matches-with-different-case",
            PowerShell("Set-Content notes.txt hello"),
            Approvals.PersistentAnywhere("set-content"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:set-content")),
        Case(
            "module-qualified-command-uses-canonical-cmdlet",
            PowerShell(@"Microsoft.PowerShell.Management\Set-Content notes.txt hello"),
            Approvals.PersistentAnywhere("Set-Content"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Set-Content")),

        Case(
            "invoke-expression-prompts",
            PowerShell("Invoke-Expression $code"),
            Approvals.None,
            ExpectedApproval.Require(["Invoke-Expression"])),
        Case(
            "invoke-expression-grant-currently-allows-dynamic-payload",
            PowerShell("Invoke-Expression $code"),
            Approvals.PersistentAnywhere("Invoke-Expression"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Invoke-Expression")),
        Case(
            "iex-alias-uses-invoke-expression-approval",
            PowerShell("iex $code"),
            Approvals.PersistentAnywhere("Invoke-Expression"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Invoke-Expression")),
        Case(
            "start-process-prompts",
            PowerShell("Start-Process notepad.exe"),
            Approvals.None,
            ExpectedApproval.Require(["Start-Process"])),

        Case(
            "dynamic-invocation-fails-closed",
            PowerShell("& $command --version"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "dynamic-path-fails-closed",
            PowerShell(@"Get-Content $env:TEMP\secret.txt"),
            Approvals.PersistentAnywhere("Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "script-block-fails-closed",
            PowerShell("Get-ChildItem | Sort-Object { Remove-Item $_ }"),
            Approvals.PersistentAnywhere("Get-ChildItem", "Sort-Object", "Remove-Item"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "calculated-property-fails-closed",
            PowerShell("Get-ChildItem | Select-Object @{N='x';E={Remove-Item $_}}"),
            Approvals.PersistentAnywhere("Get-ChildItem", "Select-Object", "Remove-Item"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "splatting-fails-closed",
            PowerShell("Get-ChildItem @args"),
            Approvals.PersistentAnywhere("Get-ChildItem"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "subexpression-fails-closed",
            PowerShell("Get-ChildItem $(Get-Content secret.txt)"),
            Approvals.PersistentAnywhere("Get-ChildItem", "Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "array-expression-fails-closed",
            PowerShell("Get-ChildItem @(Get-Content secret.txt)"),
            Approvals.PersistentAnywhere("Get-ChildItem", "Get-Content"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "foreach-script-block-fails-closed",
            PowerShell("Get-ChildItem | ForEach-Object { Remove-Item $_ }"),
            Approvals.PersistentAnywhere("Get-ChildItem", "ForEach-Object", "Remove-Item"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "unbalanced-quote-fails-closed",
            PowerShell("Set-Content notes.txt \"unterminated"),
            Approvals.None,
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0)),
        Case(
            "empty-command-fails-closed",
            PowerShell(string.Empty),
            Approvals.None,
            ExpectedApproval.Require([], approvalChecks: 0)),

        Case(
            "write-output-prompts",
            PowerShell("Write-Output hello"),
            Approvals.None,
            ExpectedApproval.Require(["Write-Output"])),
        Case(
            "echo-alias-prompts-for-write-output",
            PowerShell("echo hello"),
            Approvals.None,
            ExpectedApproval.Require(["Write-Output"])),
        Case(
            "get-command-prompts",
            PowerShell("Get-Command git"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Command"])),
        Case(
            "get-help-prompts",
            PowerShell("Get-Help Get-ChildItem"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Help"])),
        Case(
            "get-process-prompts",
            PowerShell("Get-Process"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Process"])),
        Case(
            "safe-redirect-inside-project-allows",
            PowerShell("Get-Date > result.txt"),
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.SafeVerbInTrustedScope)),
        Case(
            "safe-redirect-outside-project-prompts",
            PowerShell(@"Get-Date > C:\Windows\Temp\netclaw-approval.txt"),
            Approvals.None,
            ExpectedApproval.Require(["Get-Date"])),

        Case(
            "session-grant-allows",
            PowerShell("Set-Content notes.txt hello"),
            Approvals.Session("Set-Content"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "session:Set-Content")),
        Case(
            "other-session-grant-prompts",
            PowerShell("Set-Content notes.txt hello"),
            Approvals.SessionForOtherSession("Set-Content"),
            ExpectedApproval.Require(["Set-Content"])),
        Case(
            "persistent-anywhere-allows",
            PowerShell("Set-Content notes.txt hello"),
            Approvals.PersistentAnywhere("Set-Content"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Set-Content")),
        Case(
            "persistent-here-allows",
            PowerShell("Set-Content notes.txt hello"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "Set-Content"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Set-Content")),
        Case(
            "persistent-here-directory-mismatch-prompts",
            PowerShell("Set-Content notes.txt hello", ApprovalDirectoryShape.External),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "Set-Content"),
            ExpectedApproval.Require(["Set-Content"])),
        Case(
            "other-audience-grant-prompts",
            PowerShell("Set-Content notes.txt hello"),
            Approvals.PersistentForOtherAudience("Set-Content"),
            ExpectedApproval.Require(["Set-Content"])),
        Case(
            "mixed-session-persistent-sequence-allows",
            PowerShell("Set-Content a.txt one; Remove-Item b.txt"),
            Approvals.Combine(
                Approvals.Session("Set-Content"),
                Approvals.PersistentAnywhere("Remove-Item")),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "session:Set-Content",
                "persistent:Remove-Item")),
        Case(
            "partial-sequence-grant-prompts",
            PowerShell("Set-Content a.txt one; Remove-Item b.txt"),
            Approvals.PersistentAnywhere("Set-Content"),
            ExpectedApproval.Require(
                ["Set-Content", "Remove-Item"],
                approvalMatches: ["persistent:Set-Content"])),
        Case(
            "safe-and-stored-authority-do-not-compose",
            PowerShell("Get-Date; Set-Content notes.txt hello"),
            Approvals.PersistentAnywhere("Set-Content"),
            ExpectedApproval.Require(
                ["Get-Date", "Set-Content"],
                approvalMatches: ["persistent:Set-Content"])),

        Case(
            "four-unapproved-statements-prompt",
            PowerShell("Set-Content a.txt one; Copy-Item a.txt b.txt; Move-Item b.txt c.txt; Remove-Item c.txt"),
            Approvals.None,
            ExpectedApproval.Require(["Set-Content", "Copy-Item", "Move-Item", "Remove-Item"])),
        Case(
            "four-anywhere-grants-allow",
            PowerShell("Set-Content a.txt one; Copy-Item a.txt b.txt; Move-Item b.txt c.txt; Remove-Item c.txt"),
            Approvals.PersistentAnywhere("Set-Content", "Copy-Item", "Move-Item", "Remove-Item"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:Set-Content",
                "persistent:Copy-Item",
                "persistent:Move-Item",
                "persistent:Remove-Item")),
        Case(
            "four-one-missing-grant-prompts",
            PowerShell("Set-Content a.txt one; Copy-Item a.txt b.txt; Move-Item b.txt c.txt; Remove-Item c.txt"),
            Approvals.PersistentAnywhere("Set-Content", "Copy-Item", "Move-Item"),
            ExpectedApproval.Require(
                ["Set-Content", "Copy-Item", "Move-Item", "Remove-Item"],
                approvalMatches:
                [
                    "persistent:Set-Content",
                    "persistent:Copy-Item",
                    "persistent:Move-Item"
                ])),
        Case(
            "four-hard-deny-beats-grants",
            PowerShell("Set-Content a.txt one; Copy-Item a.txt b.txt; Stop-Process -Id 1; Remove-Item b.txt"),
            Approvals.PersistentAnywhere("Set-Content", "Copy-Item", "Stop-Process", "Remove-Item"),
            ExpectedApproval.Deny("hard_deny_self_destructive")),

        Case(
            "noninteractive-unapproved-requires-approval",
            PowerShell("Set-Content notes.txt hello", interactive: false),
            Approvals.None,
            ExpectedApproval.Require(["Set-Content"])),
        Case(
            "noninteractive-persistent-grant-allows",
            PowerShell("Set-Content notes.txt hello", interactive: false),
            Approvals.PersistentAnywhere("Set-Content"),
            ExpectedApproval.Allow(ToolAllowReason.StoredApproval, 1, "persistent:Set-Content"))
    ];

    private static readonly FrozenDictionary<string, ShellApprovalCase> CasesById =
        All.ToFrozenDictionary(testCase => testCase.Id, StringComparer.Ordinal);

    public static IEnumerable<TheoryDataRow<string>> Rows => All.Select(testCase =>
        new TheoryDataRow<string>(testCase.Id)
            .WithTestDisplayName($"PowerShell approval :: {testCase.Id}")
            .WithTrait("Disposition", testCase.Expected.Outcome.ToString())
            .WithTrait("AllowReason", testCase.Expected.AllowReason?.ToString() ?? "NotAllowed"));

    internal static ShellApprovalCase Get(string id) => CasesById[id];

    internal static string RenderReviewTable()
    {
        var lines = new List<string>
        {
            "# Fresh Personal PowerShell approval matrix",
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
        => new(id, ShellGrammar.PowerShell, invocation, approvals, expected);

    private static ShellApprovalInvocation PowerShell(
        string command,
        ApprovalDirectoryShape workingDirectory = ApprovalDirectoryShape.Project,
        TrustAudience audience = TrustAudience.Personal,
        bool interactive = true)
        => new(command, workingDirectory, audience, interactive);

    private static string EncodedCommand(string command)
        => $"pwsh -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(command))}";

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
