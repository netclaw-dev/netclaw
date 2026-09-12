// -----------------------------------------------------------------------
// <copyright file="DoctorCommandOptionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DoctorCommandOptionsTests
{
    [Fact]
    public void ParsesFixAndDryRunOptions()
    {
        var options = DoctorCommandOptions.Parse(["doctor", "--fix", "--dry-run", "--format", "json"]);

        Assert.True(options.Fix);
        Assert.True(options.DryRun);
        Assert.Equal(DoctorOutputFormat.Json, options.Format);
    }

    [Fact]
    public void DryRunImpliesFix()
    {
        var options = DoctorCommandOptions.Parse(["doctor", "--dry-run"]);

        Assert.True(options.Fix);
        Assert.True(options.DryRun);
    }
}
