using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Skills;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

internal sealed class DaemonStatsService(
    TimeProvider timeProvider,
    SessionCatalogService sessionCatalog,
    SkillRegistry skillRegistry,
    IRequiredActor<DailyStatsActorKey> dailyStatsActor,
    SQLiteMemoryStore? sqliteMemoryStore = null,
    IRequiredActor<ReminderManagerActorKey>? reminderManagerActor = null)
{
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();

    public async Task<DaemonStats.Response> GetStatsAsync(int? days = null, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var uptime = now - _startedAt;

        var tokenSnapshot = SessionTelemetry.GetSnapshot();
        var slackSnapshot = ChannelTelemetry.GetSnapshot();
        var sessionStats = sessionCatalog.GetStats();
        var allSkills = skillRegistry.GetAll();
        var enrichedKeywords = skillRegistry.GetEnrichedKeywords();

        var dailyBreakdown = days.HasValue
            ? await QueryDailyStatsAsync(days.Value, ct)
            : [];

        return new DaemonStats.Response
        {
            Process = new DaemonStats.Process
            {
                UptimeSeconds = (long)uptime.TotalSeconds,
                StartedAtUtc = _startedAt
            },
            Tokens = new DaemonStats.Tokens
            {
                InputTokensTotal = tokenSnapshot.InputTokensTotal,
                OutputTokensTotal = tokenSnapshot.OutputTokensTotal,
                TurnsCompletedTotal = tokenSnapshot.TurnsCompletedTotal,
                MemoriesFormedTotal = tokenSnapshot.MemoriesFormedTotal,
                MemoriesRecalledTotal = tokenSnapshot.MemoriesRecalledTotal,
                SkillsLoadedTotal = tokenSnapshot.SkillsLoadedTotal
            },
            Sessions = new DaemonStats.Sessions
            {
                TotalSessions = sessionStats.TotalSessions,
                ActiveSessions = sessionStats.ActiveSessions,
                TotalTurns = sessionStats.TotalTurns
            },
            Memory = await BuildMemoryStatsAsync(ct),
            Skills = new DaemonStats.Skills
            {
                TotalAvailable = allSkills.Count,
                WithEnrichedKeywords = enrichedKeywords.Count
            },
            SlackActivity = new DaemonStats.SlackActivity
            {
                EventsReceived = slackSnapshot.SlackEventsReceived,
                EventsRouted = slackSnapshot.SlackEventsRouted,
                EventsDropped = slackSnapshot.SlackEventsDropped,
                RepliesPosted = slackSnapshot.SlackRepliesPosted,
                RepliesFailed = slackSnapshot.SlackRepliesFailed
            },
            Reminders = await BuildReminderStatsAsync(ct),
            DailyBreakdown = dailyBreakdown
        };
    }

    private async Task<List<DaemonStats.DailyRow>> QueryDailyStatsAsync(int days, CancellationToken ct)
    {
        try
        {
            var actorRef = await dailyStatsActor.GetAsync(ct);
            var result = await actorRef.Ask<DailyStatsActor.QueryDailyStatsResult>(
                new DailyStatsActor.QueryDailyStats(days), TimeSpan.FromSeconds(5), ct);
            return result.Rows
                .Select(r => new DaemonStats.DailyRow
                {
                    Date = r.DateKey,
                    InputTokens = r.InputTokens,
                    OutputTokens = r.OutputTokens,
                    Turns = r.Turns,
                    Sessions = r.Sessions,
                    MemoriesFormed = r.MemoriesFormed,
                    MemoriesRecalled = r.MemoriesRecalled,
                    SkillsLoaded = r.SkillsLoaded
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<DaemonStats.Memory> BuildMemoryStatsAsync(CancellationToken ct)
    {
        if (sqliteMemoryStore is null)
        {
            return new DaemonStats.Memory { Status = "unavailable" };
        }

        try
        {
            var stats = await sqliteMemoryStore.GetStatsAsync(ct);
            return new DaemonStats.Memory
            {
                Status = "healthy",
                AnchorCount = stats.AnchorCount,
                DocumentCount = stats.DocumentCount,
                RecordCount = stats.RecordCount,
                EdgeCount = stats.EdgeCount,
                PendingCheckpoints = stats.PendingCheckpoints
            };
        }
        catch
        {
            return new DaemonStats.Memory { Status = "degraded" };
        }
    }

    private async Task<DaemonStats.Reminders?> BuildReminderStatsAsync(CancellationToken ct)
    {
        if (reminderManagerActor is null)
            return null;

        try
        {
            var actorRef = await reminderManagerActor.GetAsync(ct);
            var response = await actorRef.Ask<ReminderHealthResponse>(
                GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(3), ct);
            return new DaemonStats.Reminders
            {
                ScheduledCount = response.ScheduledCount,
                ActiveExecutions = response.ActiveExecutions,
                FailedCount = response.FailedCount
            };
        }
        catch
        {
            return null;
        }
    }
}
