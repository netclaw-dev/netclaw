// -----------------------------------------------------------------------
// <copyright file="DaemonCliArgsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon;
using Xunit;

namespace Netclaw.Daemon.Tests;

/// <summary>
/// Regression coverage for the alpha.onnx.2 production canary: <c>netclawd --version</c> used to
/// ignore the flag entirely and boot a full daemon instance (acquiring the lock file, starting
/// the host). Program.cs is top-level statements, so the arg-handling itself is extracted into
/// <see cref="DaemonCliArgs.IsVersionRequest"/> to make it unit-testable in isolation without
/// booting the host.
/// </summary>
public sealed class DaemonCliArgsTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void IsVersionRequest_returns_true_for_version_flags(string flag)
    {
        Assert.True(DaemonCliArgs.IsVersionRequest([flag]));
    }

    [Fact]
    public void IsVersionRequest_returns_false_for_no_args()
    {
        Assert.False(DaemonCliArgs.IsVersionRequest([]));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-V")]
    public void IsVersionRequest_returns_false_for_non_version_args(string arg)
    {
        Assert.False(DaemonCliArgs.IsVersionRequest([arg]));
    }

    [Fact]
    public void IsVersionRequest_only_considers_the_first_argument()
    {
        Assert.False(DaemonCliArgs.IsVersionRequest(["start", "--version"]));
    }
}
