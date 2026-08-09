// -----------------------------------------------------------------------
// <copyright file="SessionDirectoryHelperTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class SessionDirectoryHelperTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Automation_directory_rejects_an_empty_session_id()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SessionDirectoryHelper.GetSessionJobsDirectory(new SessionId(" "), _directory.Path));

        Assert.Equal("sessionId", exception.ParamName);
    }

}
