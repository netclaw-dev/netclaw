// -----------------------------------------------------------------------
// <copyright file="ChildNoticeFileReadGateSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for S2-5. The model reads a child result only through <c>file_read</c>. A requester whose profile has no
/// <c>file_read</c> could not read the result, so the runtime refuses the spawn and does not lose the result silently.
/// </summary>
public sealed class ChildNoticeFileReadGateSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    // The profile of the requester allows the spawn tool and the approval probe, and nothing else.
    protected override void ConfigurePersonalProfile(ToolAudienceProfile profile)
    {
        profile.ToolsMode = ToolProfileMode.Allowlist;
        profile.AllowedTools = ["spawn_agent", "authority_probe"];
    }

    /// <summary>
    /// Spec 18. The spawn call fails with a clear reason that names <c>file_read</c>. No child run starts.
    /// Planted bug: the spawner allows the start without a check of the requester profile.
    /// </summary>
    [Fact]
    public async Task Spawn_is_refused_when_the_requester_cannot_read_files()
    {
        // Arrange
        var sessionId = new SessionId("notice/no-file-read");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);

        // Act
        await SendAsync(sessionId, "spawn:child-a:one", Operator(Alice));
        await ExpectTurnsAsync(subscriber, 1);

        // Assert
        var result = Assert.Single(journal.Committed.OfType<ToolCallRecorded>(),
            evt => evt.ToolResult.ToolCallId?.Value == "spawn-one").ToolResult.Content;
        Note($"spawn result: {result}");
        Assert.Contains("file_read", result, StringComparison.Ordinal);
        Assert.Empty(Accepted(journal));
        Assert.Equal(0, Clients.A.CallCount);
    }
}
