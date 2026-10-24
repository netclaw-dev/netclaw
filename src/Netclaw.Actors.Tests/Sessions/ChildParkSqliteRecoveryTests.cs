// -----------------------------------------------------------------------
// <copyright file="ChildParkSqliteRecoveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Persistence.Sql.Hosting;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// PR 2 of durable-child-consent-wait (durable-child-park-recovery, task 3.3), end to end. The real serializer writes
/// the park to a real SQLite journal and snapshot store. The session recovers once from the journal and once from the
/// snapshot that its passivation saves. The fault journal is not in this class, so the spec reads only the outputs and
/// the approval probe.
/// </summary>
public sealed class ChildParkSqliteRecoveryTests(ITestOutputHelper output) : ChildParkTestBase(output)
{
    private TestSessionTempDirectory? _sqliteDirectory;

    protected override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        _sqliteDirectory = TestSessionTempDirectory.Create("netclaw-park-sqlite-");
        builder.WithSqlPersistence($"Data Source={Path.Combine(_sqliteDirectory.Path, "journal.db")}", "SQLite.MS");
    }

    protected override void DeleteOwnedDirectories()
    {
        SqliteConnection.ClearAllPools();
        _sqliteDirectory?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.DeleteOwnedDirectories();
    }

    /// <summary>
    /// A child of Alice parks. The session crashes and recovers from the SQLite journal. Then the session passivates,
    /// and its final snapshot deletes the journal rows, so the next wake recovers from the snapshot. Alice approves the
    /// old prompt, and the parked call runs once. The child then ends, and the parent gets its notice.
    /// Planted bug: the reader cannot decode the park row or the parked snapshot record, or a recovery loses the park.
    /// </summary>
    [Fact]
    public async Task A_parked_run_survives_a_SQLite_journal_and_snapshot_restart()
    {
        // Arrange
        var id = new SessionId("park/sqlite");
        await SqlPersistencePlugins.StartOneAtATimeAsync(this, ColdRecoveryCeiling);
        ScriptParent();
        PlanChild("park-sqlite", [[Probe("p1")]]);
        await JoinRecorderAsync(id);
        await SendAsync(id, "spawn:child-a:park-sqlite", AliceSource);
        ToolInteractionRequest? prompt = null;
        await AwaitAssertAsync(() => prompt = Assert.Single(Outputs<ToolInteractionRequest>(),
            request => request.CallId.Value.Contains("/subagent-approval/", StringComparison.Ordinal)), Deadline,
            cancellationToken: Cancellation);
        var runId = new SubAgentRunId(prompt!.CallId.Value[..prompt.CallId.Value.IndexOf("/subagent-approval/", StringComparison.Ordinal)]);

        // Act: a restart from the journal, and then a restart from the snapshot of the passivation.
        await RestartSessionAsync(id);
        await JoinRecorderAsync(id);
        await PassivateAsync(id);
        await JoinRecorderAsync(id);
        AssertAck(await AnswerAsync(id, prompt.CallId, Alice));

        // Assert
        await AwaitAssertAsync(() => Assert.Contains(Clients.Parent.ReceivedMessages, request => CarriesNoticeFor(request, runId)),
            Deadline, cancellationToken: Cancellation);
        await BarrierAsync(id);
        Assert.Single(ApprovalTool.Contexts);
    }
}
