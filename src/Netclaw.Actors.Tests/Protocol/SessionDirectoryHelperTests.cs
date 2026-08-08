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
    public void Automation_directories_use_the_canonical_session_directory()
    {
        var sessionId = new SessionId("C123/1712000000.000001");
        var sessionDirectory = SessionDirectoryHelper.GetSessionDirectory(sessionId, _directory.Path);

        Assert.Equal(
            Path.Combine(sessionDirectory, SessionDirectoryHelper.RemindersSubdirectory),
            SessionDirectoryHelper.GetSessionRemindersDirectory(sessionId, _directory.Path));
        Assert.Equal(
            Path.Combine(sessionDirectory, SessionDirectoryHelper.JobsSubdirectory),
            SessionDirectoryHelper.GetSessionJobsDirectory(sessionId, _directory.Path));
    }

    [Fact]
    public void Automation_directory_rejects_an_empty_session_id()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SessionDirectoryHelper.GetSessionJobsDirectory(new SessionId(" "), _directory.Path));

        Assert.Equal("sessionId", exception.ParamName);
    }

}
