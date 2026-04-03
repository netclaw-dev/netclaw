using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class CommandApprovalCacheTests
{
    [Fact]
    public void Session_approval_is_found()
    {
        var cache = new CommandApprovalCache();
        cache.ApproveForSession(TrustAudience.Personal, "shell_execute", "git push");
        Assert.True(cache.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));
    }

    [Fact]
    public void Unapproved_pattern_not_found()
    {
        var cache = new CommandApprovalCache();
        Assert.False(cache.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));
    }

    [Fact]
    public void Per_audience_isolation()
    {
        var cache = new CommandApprovalCache();
        cache.ApproveForSession(TrustAudience.Personal, "shell_execute", "git push");

        Assert.True(cache.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));
        Assert.False(cache.IsApproved(TrustAudience.Team, "shell_execute", "git push"));
    }

    [Fact]
    public void Per_tool_isolation()
    {
        var cache = new CommandApprovalCache();
        cache.ApproveForSession(TrustAudience.Personal, "shell_execute", "git push");

        Assert.True(cache.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));
        Assert.False(cache.IsApproved(TrustAudience.Personal, "file_write", "git push"));
    }

    [Fact]
    public void Prefix_match_broader_approval_covers_specific()
    {
        var cache = new CommandApprovalCache();
        cache.ApproveForSession(TrustAudience.Personal, "shell_execute", "git");

        // Approving "git" should cover "git push"
        Assert.True(cache.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));
    }

    [Fact]
    public void Persistent_approval_also_cached_in_session()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var store = new ToolApprovalStore(tempFile);
            var cache = new CommandApprovalCache(store);

            cache.ApprovePersistent(TrustAudience.Personal, "shell_execute", "git push");

            // Should be found in session cache
            Assert.True(cache.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));

            // A new cache backed by the same store should also find it
            var cache2 = new CommandApprovalCache(store);
            Assert.True(cache2.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Case_insensitive_match()
    {
        var cache = new CommandApprovalCache();
        cache.ApproveForSession(TrustAudience.Personal, "shell_execute", "Git Push");
        Assert.True(cache.IsApproved(TrustAudience.Personal, "shell_execute", "git push"));
    }
}
