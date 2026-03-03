using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ToolPathPolicyTests
{
    [Fact]
    public void IsDenied_blocks_exact_match()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        Assert.True(policy.IsDenied("/home/user/.netclaw/config/secrets.json"));
    }

    [Fact]
    public void IsDenied_allows_non_matching_path()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        Assert.False(policy.IsDenied("/home/user/.netclaw/config/netclaw.json"));
    }

    [Fact]
    public void IsDenied_normalizes_path_traversal()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        // Path with .. that resolves to the denied path
        Assert.True(policy.IsDenied("/home/user/.netclaw/config/../config/secrets.json"));
    }

    [Fact]
    public void IsDenied_case_insensitive()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/Secrets.json"]);
        Assert.True(policy.IsDenied("/home/user/.netclaw/config/secrets.json"));
    }

    [Fact]
    public void IsDenied_returns_false_for_empty_path()
    {
        var policy = new ToolPathPolicy(["/some/path"]);
        Assert.False(policy.IsDenied(""));
        Assert.False(policy.IsDenied("  "));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_path_in_command()
    {
        var secretsPath = "/home/user/.netclaw/config/secrets.json";
        var policy = new ToolPathPolicy([secretsPath]);

        Assert.True(policy.CommandReferencesDeniedPath($"cat {secretsPath}"));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {secretsPath} | jq ."));
    }

    [Fact]
    public void CommandReferencesDeniedPath_allows_safe_commands()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        Assert.False(policy.CommandReferencesDeniedPath("ls -la /tmp"));
        Assert.False(policy.CommandReferencesDeniedPath("echo hello"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_returns_false_for_empty()
    {
        var policy = new ToolPathPolicy(["/some/path"]);
        Assert.False(policy.CommandReferencesDeniedPath(""));
        Assert.False(policy.CommandReferencesDeniedPath("  "));
    }

    [Fact]
    public void Multiple_denied_paths()
    {
        var policy = new ToolPathPolicy(["/path/a", "/path/b"]);
        Assert.True(policy.IsDenied("/path/a"));
        Assert.True(policy.IsDenied("/path/b"));
        Assert.False(policy.IsDenied("/path/c"));
    }

    [Fact]
    public void IsDenied_blocks_children_of_denied_directory()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/keys"]);
        Assert.True(policy.IsDenied("/home/user/.netclaw/keys/keyring.xml"));
    }

    [Fact]
    public void IsDenied_does_not_match_prefix_without_path_boundary()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/keys"]);
        Assert.False(policy.IsDenied("/home/user/.netclaw/keys-backup/data.txt"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_home_shorthand()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var policy = new ToolPathPolicy([Path.Combine(home, ".netclaw", "config", "secrets.json")]);

        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/config/secrets.json"));
        Assert.True(policy.CommandReferencesDeniedPath("cat $HOME/.netclaw/config/secrets.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_keys_directory_access()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/keys"]);

        Assert.True(policy.CommandReferencesDeniedPath("ls ~/.netclaw/keys"));
        Assert.True(policy.CommandReferencesDeniedPath("tar czf /tmp/k.tgz ~/.netclaw/keys"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_high_risk_glob_in_config_directory()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);

        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/config/*.json"));
        Assert.True(policy.CommandReferencesDeniedPath("jq . ~/.netclaw/config/*.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_high_risk_archive_of_config_directory()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);

        Assert.True(policy.CommandReferencesDeniedPath("tar czf /tmp/netclaw-config.tgz ~/.netclaw/config"));
    }
}
