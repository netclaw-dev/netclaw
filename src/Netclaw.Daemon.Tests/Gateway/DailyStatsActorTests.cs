// -----------------------------------------------------------------------
// <copyright file="DailyStatsActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class DailyStatsActorTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly ActorSystem _system;

    public DailyStatsActorTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _system = ActorSystem.Create($"daily-stats-tests-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task QuerySkillUsageStats_returns_groupable_rows_for_each_method()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero));
        var actor = _system.ActorOf(Props.Create(() => new DailyStatsActor(
            _paths,
            time,
            NullLogger<DailyStatsActor>.Instance)));

        actor.Tell(new DailyStatsActor.RecordSkillLoaded("netclaw-operations", SkillLoadMethod.FileRead));
        actor.Tell(new DailyStatsActor.RecordSkillLoaded("netclaw-operations", SkillLoadMethod.FileRead));
        actor.Tell(new DailyStatsActor.RecordSkillLoaded("create-release", SkillLoadMethod.SkillLoadTool));
        actor.Tell(new DailyStatsActor.RecordSkillLoaded("create-release", SkillLoadMethod.SlashCommand));

        var rows = await actor.Ask<DailyStatsActor.QuerySkillUsageStatsResult>(
            new DailyStatsActor.QuerySkillUsageStats(7),
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Rows.Count);

        var opsFileRead = Assert.Single(rows.Rows, r => r.SkillName == "netclaw-operations" && r.Method == SkillLoadMethod.FileRead);
        Assert.Equal(2, opsFileRead.Count);

        var releaseRows = rows.Rows.Where(r => r.SkillName == "create-release").OrderBy(r => r.Method.ToWireValue()).ToList();
        Assert.Equal(2, releaseRows.Count);
        Assert.Contains(releaseRows, r => r.Method == SkillLoadMethod.SkillLoadTool && r.Count == 1);
        Assert.Contains(releaseRows, r => r.Method == SkillLoadMethod.SlashCommand && r.Count == 1);

        var totals = await actor.Ask<DailyStatsActor.ProcessStatsResult>(
            new DailyStatsActor.QueryProcessStats(),
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, totals.SkillsLoadedTotal);
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }


}
