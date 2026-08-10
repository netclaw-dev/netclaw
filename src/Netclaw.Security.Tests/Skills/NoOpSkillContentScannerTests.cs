// -----------------------------------------------------------------------
// <copyright file="NoOpSkillContentScannerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security.Skills;
using Xunit;

namespace Netclaw.Security.Tests.Skills;

public class NoOpSkillContentScannerTests
{
    [Fact]
    public async Task ScanAsync_allows_all_content()
    {
        var scanner = new NoOpSkillContentScanner();

        var result = await scanner.ScanAsync("test-skill", "any content here", TestContext.Current.CancellationToken);

        Assert.Equal(ScanVerdict.Allowed, result.Verdict);
        Assert.True(result.IsAllowed);
        Assert.Equal(ScanVerdict.Allowed, result.Verdict);
        Assert.Null(result.Reason);
    }

}
