using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellCommandPolicyTests
{
    private readonly ShellCommandPolicy _policy = new();

    // ── Self-destruction: daemon stop ──

    [Fact]
    public void Denies_netclaw_daemon_stop()
    {
        var decision = _policy.Evaluate("netclaw daemon stop");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Denies_netclaw_daemon_kill()
    {
        var decision = _policy.Evaluate("netclaw daemon kill");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Denies_systemctl_stop_netclaw()
    {
        var decision = _policy.Evaluate("systemctl stop netclaw");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    // ── Self-destruction: process killing ──

    [Fact]
    public void Denies_kill_command()
    {
        var decision = _policy.Evaluate("kill -9 12345");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Denies_killall_command()
    {
        var decision = _policy.Evaluate("killall netclaw");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Denies_pkill_command()
    {
        var decision = _policy.Evaluate("pkill -f netclaw");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    // ── System-destructive: rm -rf ──

    [Fact]
    public void Denies_rm_rf_root()
    {
        var decision = _policy.Evaluate("rm -rf /");
        Assert.False(decision.Allowed);
        Assert.Equal("system_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Denies_rm_rf_home()
    {
        var decision = _policy.Evaluate("rm -rf ~");
        Assert.False(decision.Allowed);
        Assert.Equal("system_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Denies_rm_rf_home_env()
    {
        var decision = _policy.Evaluate("rm -rf $HOME");
        Assert.False(decision.Allowed);
        Assert.Equal("system_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Allows_rm_rf_specific_directory()
    {
        var decision = _policy.Evaluate("rm -rf /tmp/build-output");
        Assert.True(decision.Allowed);
    }

    // ── System-destructive: mkfs ──

    [Fact]
    public void Denies_mkfs()
    {
        var decision = _policy.Evaluate("mkfs.ext4 /dev/sda1");
        Assert.False(decision.Allowed);
        Assert.Equal("system_destructive", decision.DenyCategory);
    }

    // ── Fork bombs ──

    [Fact]
    public void Denies_fork_bomb()
    {
        var decision = _policy.Evaluate(":(){ :|:& };:");
        Assert.False(decision.Allowed);
        Assert.Equal("system_destructive", decision.DenyCategory);
    }

    // ── Safe commands allowed ──

    [Fact]
    public void Allows_git_push()
    {
        var decision = _policy.Evaluate("git push origin main");
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Allows_ls()
    {
        var decision = _policy.Evaluate("ls -la /tmp");
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Allows_dotnet_build()
    {
        var decision = _policy.Evaluate("dotnet build");
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Allows_git_status()
    {
        var decision = _policy.Evaluate("git status");
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Allows_empty_command()
    {
        var decision = _policy.Evaluate("");
        Assert.True(decision.Allowed);
    }

    // ── Compound commands ──

    [Fact]
    public void Denies_compound_with_denied_segment()
    {
        var decision = _policy.Evaluate("echo hello && netclaw daemon stop");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Allows_compound_of_safe_commands()
    {
        var decision = _policy.Evaluate("git add . && git commit -m fix && git push");
        Assert.True(decision.Allowed);
    }

    // ── bash -c recursion ──

    [Fact]
    public void Denies_bash_c_wrapping_denied_command()
    {
        var decision = _policy.Evaluate("bash -c \"netclaw daemon stop\"");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Allows_bash_c_wrapping_safe_command()
    {
        var decision = _policy.Evaluate("bash -c \"git status\"");
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Denies_bash_lc_wrapping_denied_command()
    {
        var decision = _policy.Evaluate("bash -lc \"netclaw daemon stop\"");
        Assert.False(decision.Allowed);
        Assert.Equal("self_destructive", decision.DenyCategory);
    }

    // ── Case insensitivity ──

    [Fact]
    public void Denies_case_insensitive_netclaw_daemon_stop()
    {
        var decision = _policy.Evaluate("Netclaw Daemon Stop");
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Denies_case_insensitive_kill()
    {
        var decision = _policy.Evaluate("KILL -9 123");
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Denies_rm_with_split_short_flags_targeting_root()
    {
        var decision = _policy.Evaluate("rm -r -f /");
        Assert.False(decision.Allowed);
        Assert.Equal("system_destructive", decision.DenyCategory);
    }

    [Fact]
    public void Denies_rm_with_long_flags_targeting_root()
    {
        var decision = _policy.Evaluate("rm --recursive --force /");
        Assert.False(decision.Allowed);
        Assert.Equal("system_destructive", decision.DenyCategory);
    }

    // ── Custom patterns ──

    [Fact]
    public void Custom_pattern_added_and_enforced()
    {
        var policy = new ShellCommandPolicy(additionalDenyPatterns: ["docker rm"]);
        var decision = policy.Evaluate("docker rm my-container");
        Assert.False(decision.Allowed);
        Assert.Equal("custom_deny", decision.DenyCategory);
    }

    [Fact]
    public void Custom_pattern_does_not_affect_unrelated_commands()
    {
        var policy = new ShellCommandPolicy(additionalDenyPatterns: ["docker rm"]);
        var decision = policy.Evaluate("docker build -t myapp .");
        Assert.True(decision.Allowed);
    }
}
