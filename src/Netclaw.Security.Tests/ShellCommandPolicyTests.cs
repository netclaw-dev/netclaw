// -----------------------------------------------------------------------
// <copyright file="ShellCommandPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
}
