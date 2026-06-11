// -----------------------------------------------------------------------
// <copyright file="DaemonStatsService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Skills;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Webhooks;

namespace Netclaw.Daemon.Gateway;

internal sealed class DaemonStatsService(
    DaemonStartClock startClock,
    TimeProvider timeProvider,
    SessionCatalogService sessionCatalog,
    SkillRegistry skillRegistry,
    IChannelRegistry channelRegistry,
    IRequiredActor<DailyStatsActorKey> dailyStatsActor,
    WebhookRouteCatalog webhookRouteCatalog,
    SQLiteMemoryStore? sqliteMemoryStore = null,
    IRequiredActor<ReminderManagerActorKey>? reminderManagerActor = null)
{
    public async Task<DaemonStats.Response> GetStatsAsync(int? days = null, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var uptime = now - startClock.StartedAt;

        var actorRef = await dailyStatsActor.GetAsync(ct);

        // Ask for process-lifetime counters and daily breakdown concurrently
        var processTask = actorRef.Ask<DailyStatsActor.ProcessStatsResult>(
            new DailyStatsActor.QueryProcessStats(), TimeSpan.FromSeconds(5), ct);
        var dailyTask = days.HasValue
            ? QueryDailyStatsAsync(actorRef, days.Value, ct)
            : Task.FromResult<List<DaemonStats.DailyRow>>([]);

        var processStats = await processTask;
        var dailyBreakdown = await dailyTask;

        var sessionStats = sessionCatalog.GetStats();
        var allSkills = skillRegistry.GetAll();

        return new DaemonStats.Response
        {
            Process = new DaemonStats.Process
            {
                UptimeSeconds = (long)uptime.TotalSeconds,
                StartedAtUtc = startClock.StartedAt
            },
            Tokens = new DaemonStats.Tokens
            {
                InputTokensTotal = processStats.InputTokensTotal,
                OutputTokensTotal = processStats.OutputTokensTotal,
                TurnsCompletedTotal = processStats.TurnsCompletedTotal,
                MemoriesFormedTotal = processStats.MemoriesFormedTotal,
                MemoriesRecalledTotal = processStats.MemoriesRecalledTotal,
                SkillsLoadedTotal = processStats.SkillsLoadedTotal
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
                TotalAvailable = allSkills.Count
            },
            Channels = BuildChannelActivityList(channelRegistry),
            Webhooks = BuildWebhookStats(),
            Reminders = await BuildReminderStatsAsync(ct),
            DailyBreakdown = dailyBreakdown
        };
    }

    public async Task<SkillUsageStats.Response> GetSkillUsageStatsAsync(int? days = null, CancellationToken ct = default)
    {
        var actorRef = await dailyStatsActor.GetAsync(ct);
        var rows = await QuerySkillUsageAsync(actorRef, days ?? 7, ct);

        var daily = rows
            .GroupBy(r => r.DateKey, StringComparer.Ordinal)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(group => new SkillUsageStats.DailySkillRow
            {
                Date = group.Key,
                TotalLoads = group.Sum(x => x.Count),
                Methods = [.. group
                    .GroupBy(x => x.Method)
                    .Select(methodGroup => new SkillUsageStats.MethodCount
                    {
                        Method = methodGroup.Key.ToWireValue(),
                        Count = methodGroup.Sum(x => x.Count)
                    })
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.Method, StringComparer.Ordinal)],
                Skills = [.. group
                    .GroupBy(x => x.SkillName, StringComparer.OrdinalIgnoreCase)
                    .Select(skillGroup => new SkillUsageStats.SkillCount
                    {
                        SkillName = skillGroup.Key,
                        TotalLoads = skillGroup.Sum(x => x.Count),
                        Methods = [.. skillGroup
                            .GroupBy(x => x.Method)
                            .Select(methodGroup => new SkillUsageStats.MethodCount
                            {
                                Method = methodGroup.Key.ToWireValue(),
                                Count = methodGroup.Sum(x => x.Count)
                            })
                            .OrderByDescending(x => x.Count)
                            .ThenBy(x => x.Method, StringComparer.Ordinal)]
                    })
                    .OrderByDescending(x => x.TotalLoads)
                    .ThenBy(x => x.SkillName, StringComparer.OrdinalIgnoreCase)]
            })
            .ToList();

        return new SkillUsageStats.Response
        {
            Daily = daily
        };
    }

    internal static List<DaemonStats.ChannelActivity> BuildChannelActivityList(IChannelRegistry registry)
    {
        var enabledChannelTypes = registry.ListChannels()
            .Where(descriptor => descriptor.IsEnabled)
            .Select(descriptor => descriptor.ChannelType)
            .ToHashSet();

        return [.. ChannelTelemetry.GetAllSnapshots()
            .Where(s => enabledChannelTypes.Contains(s.ChannelType))
            .Select(s => s.ToWireActivity())];
    }

    private static async Task<List<DaemonStats.DailyRow>> QueryDailyStatsAsync(IActorRef actorRef, int days, CancellationToken ct)
    {
        try
        {
            var result = await actorRef.Ask<DailyStatsActor.QueryDailyStatsResult>(
                new DailyStatsActor.QueryDailyStats(days), TimeSpan.FromSeconds(5), ct);
            return [.. result.Rows
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
                })];
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<DailyStatsActor.DailySkillUsageRow>> QuerySkillUsageAsync(IActorRef actorRef, int days, CancellationToken ct)
    {
        try
        {
            var result = await actorRef.Ask<DailyStatsActor.QuerySkillUsageStatsResult>(
                new DailyStatsActor.QuerySkillUsageStats(days), TimeSpan.FromSeconds(5), ct);
            return result.Rows;
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

    private DaemonStats.Webhooks BuildWebhookStats()
    {
        var counts = webhookRouteCatalog.GetRouteCounts();
        var snapshot = WebhookTelemetry.GetSnapshot();
        return new DaemonStats.Webhooks
        {
            TotalRoutes = counts.Total,
            EnabledRoutes = counts.Enabled,
            DisabledRoutes = counts.Disabled,
            InvalidRoutes = counts.Invalid,
            Accepted = snapshot.Accepted,
            RouteNotFound = snapshot.RouteNotFound,
            VerificationFailed = snapshot.VerificationFailed,
            BodyTooLarge = snapshot.BodyTooLarge,
            InvalidJson = snapshot.InvalidJson,
            RateLimited = snapshot.RateLimited,
            EventFiltered = snapshot.EventFiltered,
            DuplicateDelivery = snapshot.DuplicateDelivery,
        };
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
