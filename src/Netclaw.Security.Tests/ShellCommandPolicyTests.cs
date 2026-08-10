// -----------------------------------------------------------------------
// <copyright file="ShellCommandPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellCommandPolicyTests
{
    private readonly ShellCommandPolicy _policy = new();

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
    [InlineData("sudo bash -lc \"git status\"")]
    [InlineData("sudo /bin/bash -lc \"git status\"")]
    public void Denies_privilege_escalation_commands(string command)
    {
        var decision = _policy.Evaluate(command);
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
        var decision = _policy.Evaluate(command);
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
    [InlineData("dash -c \"netclaw daemon stop\"")]
    [InlineData("/bin/dash -c \"netclaw daemon stop\"")]
    [InlineData("/usr/bin/zsh -c \"netclaw daemon stop\"")]
    [InlineData("ksh -c \"netclaw daemon stop\"")]
    public void Denies_bourne_shell_wrapping_denied_command(string command)
    {
        var decision = _policy.EvaluateBash(command);
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Fact]
    public void Bash_does_not_interpret_power_shell_child_source()
    {
        var decision = _policy.EvaluateBash(
            "pwsh -NoProfile -NonInteractive -Command 'netclaw daemon stop'");

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Native_power_shell_denies_same_language_child_hard_deny_command()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var policy = new ShellCommandPolicy(environment);

        var decision = policy.Evaluate(
            "pwsh -NoProfile -Command 'netclaw daemon stop'",
            @"C:\work");

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Theory]
    [InlineData("Start-Process pwsh -Verb RunAs")]
    [InlineData("Start-Process pwsh -Ve RunAs")]
    [InlineData("Start-Process pwsh -V 'RunAs'")]
    [InlineData("Start-Process pwsh -Verb:\"RunAs\"")]
    [InlineData("saps pwsh -Verb RunAs")]
    public void Native_power_shell_denies_elevation_parameter_forms(string command)
    {
        var policy = PowerShellPolicy();

        var decision = policy.Evaluate(command, @"C:\work");

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.PrivilegeEscalation, decision.DenyCategory);
    }

    [Fact]
    public void Native_power_shell_does_not_treat_verbose_as_the_verb_parameter()
    {
        var decision = PowerShellPolicy().Evaluate(
            "Start-Process pwsh -Verbose RunAs",
            @"C:\work");

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData(@"Remove-Item C:\ -Recurse")]
    [InlineData(@"Remove-Item 'C:\' -Re")]
    [InlineData(@"Remove-Item -LiteralPath FileSystem::C:\ -R -Confirm:$false")]
    [InlineData(@"Remove-Item -Path:C:\ -Recurse")]
    [InlineData(@"ri C:\ -Recurse")]
    public void Native_power_shell_denies_recursive_root_removal_without_force(string command)
    {
        var decision = PowerShellPolicy().Evaluate(command, @"C:\work");

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SystemDestructive, decision.DenyCategory);
    }

    [Theory]
    [InlineData(@"Remove-Item C:\ -Force")]
    [InlineData(@"Remove-Item C:\ -Recurse:$false -Force")]
    public void Native_power_shell_does_not_categorically_deny_non_recursive_root_removal(
        string command)
    {
        var decision = PowerShellPolicy().Evaluate(command, @"C:\work");

        Assert.True(decision.Allowed);
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
        var decision = _policy.Evaluate(command);
        Assert.False(decision.Allowed);
    }

    // ── Custom patterns ──

    [Fact]
    public void Custom_pattern_added_and_enforced()
    {
        var policy = new ShellCommandPolicy(additionalDenyPatterns: ["docker rm"]);
        var decision = policy.Evaluate("docker rm my-container");
        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.CustomDeny, decision.DenyCategory);
    }

    [Fact]
    public void Custom_pattern_does_not_affect_unrelated_commands()
    {
        var policy = new ShellCommandPolicy(additionalDenyPatterns: ["docker rm"]);
        var decision = policy.Evaluate("docker build -t myapp .");
        Assert.True(decision.Allowed);
    }

    private static ShellCommandPolicy PowerShellPolicy()
        => new(ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7));
}
