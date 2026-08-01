// -----------------------------------------------------------------------
// <copyright file="ShellCommandPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;
using Netclaw.Tools;

namespace Netclaw.Security.Tests;

public sealed class ShellCommandPolicyTests
{
    private readonly ShellCommandPolicy _policy = new(ShellExecutionEnvironment.Current);

    // ── Self-destruction ──

    [Theory]
    [InlineData("netclaw daemon stop")]
    [InlineData("netclaw daemon kill")]
    [InlineData("systemctl stop netclaw")]
    [InlineData("kill -9 12345")]
    [InlineData("killall netclaw")]
    [InlineData("pkill -f netclaw")]
    public void Denies_self_destructive_commands(string command)
    {
        var decision = _policy.Evaluate(command);
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    // ── Privilege escalation ──

    [Theory]
    [InlineData("sudo rm -rf /tmp/build")]
    [InlineData("su -c 'whoami'")]
    [InlineData("doas apt install curl")]
    [InlineData("echo hello && sudo kill -9 123")]
    [InlineData("SUDO rm -rf /tmp")]
    [InlineData("bash -c \"sudo kill -9 123\"")]
    public void Denies_privilege_escalation_commands(string command)
    {
        // Every row is Bash/POSIX grammar (sudo/su/doas, bash -c recursion), so
        // pin the Bash environment to exercise that grammar deterministically on
        // any host. PowerShell privilege-escalation coverage lives in
        // PowerShell_canonical_verbs_enforce_native_hard_denies.
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.Bash());
        var decision = policy.Evaluate(command);
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.PrivilegeEscalation, decision.DenyCategory);
    }

    // ── System-destructive ──

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm -rf ~")]
    [InlineData("rm -rf $HOME")]
    [InlineData("mkfs.ext4 /dev/sda1")]
    [InlineData(":(){ :|:& };:")]
    public void Denies_system_destructive_commands(string command)
    {
        // POSIX destructive shapes (rm -rf /, ~, $HOME, mkfs, fork bomb) are
        // Bash grammar; pin the Bash environment so they parse identically on
        // Windows. PowerShell's Remove-Item destructive coverage is separate.
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.Bash());
        var decision = policy.Evaluate(command);
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SystemDestructive, decision.DenyCategory);
    }

    [Fact]
    public void Allows_rm_rf_specific_directory()
    {
        var decision = _policy.Evaluate("rm -rf /tmp/build-output");
        Assert.True(decision.Allowed);
    }

    // ── Safe commands allowed ──

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("ls -la /tmp")]
    [InlineData("dotnet build")]
    [InlineData("git status")]
    [InlineData("")]
    public void Allows_safe_commands(string command)
    {
        var decision = _policy.Evaluate(command);
        Assert.True(decision.Allowed);
    }

    // ── Compound commands ──

    [Fact]
    public void Denies_compound_with_denied_segment()
    {
        var decision = _policy.Evaluate("echo hello && netclaw daemon stop");
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Theory]
    [InlineData("echo safe | netclaw daemon stop")]
    [InlineData("printf safe | sudo kill -9 123")]
    [InlineData("bash -c \"echo safe | netclaw daemon stop\"")]
    public void Denies_pipeline_with_denied_tail(string command)
    {
        var decision = _policy.EvaluateBash(command);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void PowerShell_parser_denies_process_kill_in_pipeline_tail()
    {
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.PowerShell());

        var decision = policy.Evaluate("Write-Output safe | Stop-Process -Id 1");

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Theory]
    [InlineData("spps -Id 1", DenyCategory.SelfDestructive)]
    [InlineData("Write-Output safe | spps -Id 1", DenyCategory.SelfDestructive)]
    [InlineData("Microsoft.PowerShell.Management\\Stop-Process -Id 1", DenyCategory.SelfDestructive)]
    [InlineData(@"Remove-Item -Recurse -Force C:\", DenyCategory.SystemDestructive)]
    [InlineData(@"rm -Recurse -Force C:\", DenyCategory.SystemDestructive)]
    [InlineData(@"Microsoft.PowerShell.Management\Remove-Item -Rec -For C:\", DenyCategory.SystemDestructive)]
    [InlineData("Start-Process pwsh -Verb RunAs", DenyCategory.PrivilegeEscalation)]
    [InlineData("Microsoft.PowerShell.Management\\Start-Process pwsh -V RunAs", DenyCategory.PrivilegeEscalation)]
    public void PowerShell_canonical_verbs_enforce_native_hard_denies(
        string command,
        DenyCategory expectedCategory)
    {
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.PowerShell());

        var decision = policy.Evaluate(command);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCategory, decision.DenyCategory);
    }

    [Fact]
    public void PowerShell_encoded_alias_cannot_bypass_hard_deny()
    {
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.PowerShell());
        var payload = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("spps -Id 1"));

        var decision = policy.Evaluate($"pwsh -EncodedCommand {payload}");

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Theory]
    [InlineData("cmd.exe /c \"echo unsafe\"")]
    [InlineData("powershell.exe -Command \"Write-Output unsafe\"")]
    [InlineData("pwsh -Command \"cmd.exe /c echo unsafe\"")]
    [InlineData("Start-Process cmd.exe -ArgumentList /c,whoami")]
    [InlineData("Start-Process powershell.exe -ArgumentList -Command,Get-Date")]
    [InlineData("Start-Process pwsh -ArgumentList -Command,Get-Date")]
    public void PowerShell_rejects_unsupported_shell_wrappers(string command)
    {
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.PowerShell());

        var decision = policy.Evaluate(command);

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.CustomDeny, decision.DenyCategory);
    }

    [Fact]
    public void Unparseable_canonical_grammar_requires_approval_instead_of_becoming_safe()
    {
        var environment = ShellExecutionEnvironment.PowerShell();
        var policy = new ShellCommandPolicy(environment);
        var matcher = new ShellApprovalMatcher(environment);
        var arguments = new Dictionary<string, object?> { ["Command"] = "if (" };

        var decision = policy.Evaluate("if (");

        Assert.True(decision.Allowed);
        Assert.True(matcher.IsMessy(new ToolName("shell_execute"), arguments));
        Assert.Empty(matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));
    }

    [Fact]
    public void Allows_compound_of_safe_commands()
    {
        var decision = _policy.Evaluate("git add . && git commit -m fix && git push");
        Assert.True(decision.Allowed);
    }

    // ── bash -c recursion ──

    [Theory]
    [InlineData("bash -c \"netclaw daemon stop\"")]
    [InlineData("bash -lc \"netclaw daemon stop\"")]
    [InlineData("bash --noprofile -lc \"netclaw daemon stop\"")]
    [InlineData("env bash -lc \"netclaw daemon stop\"")]
    [InlineData("command bash -lc \"netclaw daemon stop\"")]
    [InlineData("timeout 5 bash -lc \"netclaw daemon stop\"")]
    [InlineData("nice -n 5 bash -lc \"netclaw daemon stop\"")]
    [InlineData("bash -c \"echo safe\" && bash -lc \"netclaw daemon stop\"")]
    public void Denies_bash_wrapping_denied_command(string command)
    {
        var decision = _policy.EvaluateBash(command);
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Fact]
    public void Allows_bash_c_wrapping_safe_command()
    {
        var decision = _policy.Evaluate("bash -c \"git status\"");
        Assert.True(decision.Allowed);
    }

    // ── Case insensitivity and flag variants ──

    [Theory]
    [InlineData("Netclaw Daemon Stop")]
    [InlineData("KILL -9 123")]
    [InlineData("rm -r -f /")]
    [InlineData("rm --recursive --force /")]
    public void Denies_case_insensitive_and_flag_variants(string command)
    {
        // All rows are Bash/POSIX shapes (netclaw daemon stop, kill, rm flag
        // variants); pin the Bash grammar so the case-insensitive verb and flag
        // normalization is exercised deterministically on any host.
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.Bash());
        var decision = policy.Evaluate(command);
        Assert.False(decision.Allowed);
    }

    // ── Custom patterns ──

    [Fact]
    public void Custom_pattern_added_and_enforced()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: ["docker rm"]);
        var decision = policy.Evaluate("docker rm my-container");
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.CustomDeny, decision.DenyCategory);
    }

    [Fact]
    public void Custom_pattern_does_not_affect_unrelated_commands()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: ["docker rm"]);
        var decision = policy.Evaluate("docker build -t myapp .");
        Assert.True(decision.Allowed);
    }
}
