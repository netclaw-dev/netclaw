// -----------------------------------------------------------------------
// <copyright file="ReminderCliEndedOneShotTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Reminder;
using Netclaw.Configuration;
using Netclaw.Daemon.Reminders;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Cli.Tests.Reminder;

/// <summary>
/// What <c>netclaw reminder show|status|cancel</c> print for an ended one-shot (change
/// <c>oneshot-reminder-autodelete</c>, design D7). The tests run the real CLI through the real
/// <see cref="DaemonApi"/> and the real routes. Only the reminder manager is a stand-in that replies
/// with canned protocol answers.
/// </summary>
public sealed class ReminderCliEndedOneShotTests : IAsyncDisposable
{
    private const string Prompt = "Check PR 42 and tell me when CI is green.";
    private static readonly DateTimeOffset EndedAt = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PurgeAt = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    private readonly DisposableTempDir _dir = new();
    private readonly ActorSystem _actorSystem = ActorSystem.Create($"reminder-cli-ended-{Guid.NewGuid():N}");
    private readonly CannedAnswers _answers = new();

    public async ValueTask DisposeAsync()
    {
        await _actorSystem.Terminate();
        _dir.Dispose();
    }

    private static object Ended(string outcome, string? lastError = null, string? instructions = null)
        => ReminderEndedProtocol.NewEndedInfo(outcome, EndedAt, PurgeAt, lastError, instructions);

    private static ReminderStatusResponse EndedStatus(object ended)
        => (ReminderStatusResponse)ReminderEndedProtocol.Construct(
            typeof(ReminderStatusResponse),
            new Dictionary<string, object?>
            {
                ["Id"] = new ReminderId("check-pr-42"),
                ["Found"] = true,
                ["Enabled"] = false,
                ["Executing"] = false,
                ["ConsecutiveFailures"] = 0,
                ["SkippedDuplicates"] = 0,
                ["RecentHistory"] = (IReadOnlyList<HistoryRecord>)
                [
                    new HistoryRecord(EndedAt.AddSeconds(-3), true, 1520, "session-1", null)
                ],
                ["Ended"] = ended
            });

    private static ReminderCancelledResponse Cancelled(bool found, object? alreadyEnded = null, bool deleted = false)
    {
        // Pass only what the case needs, so a case that needs no new member does not depend on one.
        var named = new Dictionary<string, object?> { ["Id"] = new ReminderId("check-pr-42"), ["Found"] = found };
        if (alreadyEnded is not null)
            named["AlreadyEnded"] = alreadyEnded;
        if (deleted)
            named["Deleted"] = true;
        return (ReminderCancelledResponse)ReminderEndedProtocol.Construct(typeof(ReminderCancelledResponse), named);
    }

    [Fact]
    public async Task Show_of_an_ended_oneshot_exits_1_and_names_the_end_and_the_purge_time()
    {
        _answers.Get = (GetReminderResponse)ReminderEndedProtocol.Construct(
            typeof(GetReminderResponse),
            new Dictionary<string, object?> { ["Reminder"] = null, ["Ended"] = Ended("Completed") });
        await using var app = await StartDaemonAsync();

        var result = await RunAsync(app, "reminder", "show", "check-pr-42");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] Reminder 'check-pr-42' ended (completed) at 2026-10-16 10:00:00Z", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("history stays readable until 2026-10-28 10:00:00Z", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("netclaw reminder history check-pr-42", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public async Task Status_of_an_ended_failed_oneshot_prints_the_end_the_error_and_the_prompt()
    {
        _answers.Status = EndedStatus(Ended("Failed", "model endpoint is not available", Prompt));
        await using var app = await StartDaemonAsync();

        var result = await RunAsync(app, "reminder", "status", "check-pr-42");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("failed at 2026-10-16 10:00:00Z", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("2026-10-28 10:00:00Z", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("model endpoint is not available", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(Prompt, result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Recent history", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_of_an_ended_completed_oneshot_prints_the_end_and_no_prompt()
    {
        _answers.Status = EndedStatus(Ended("Completed"));
        await using var app = await StartDaemonAsync();

        var result = await RunAsync(app, "reminder", "status", "check-pr-42");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("completed at 2026-10-16 10:00:00Z", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Prompt:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Prompt, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_prints_what_the_cancel_did()
    {
        await using var app = await StartDaemonAsync();

        _answers.Cancel = Cancelled(found: true, deleted: true);
        var oneShot = await RunAsync(app, "reminder", "cancel", "check-pr-42");
        _answers.Cancel = Cancelled(found: true);
        var recurring = await RunAsync(app, "reminder", "cancel", "check-pr-42");
        _answers.Cancel = Cancelled(found: true, alreadyEnded: Ended("Completed"));
        var ended = await RunAsync(app, "reminder", "cancel", "check-pr-42");

        Assert.True(oneShot.ExitCode == 0, oneShot.Stderr);
        Assert.Equal("Reminder 'check-pr-42' cancelled and deleted.", oneShot.Stdout);
        Assert.Equal("Reminder 'check-pr-42' cancelled (disabled).", recurring.Stdout);
        Assert.True(ended.ExitCode == 0, ended.Stderr);
        Assert.Contains("already ended (completed at 2026-10-16 10:00:00Z)", ended.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_says_that_cancel_deletes_a_oneshot_and_disables_a_recurring_reminder()
    {
        await using var app = await StartDaemonAsync();

        var result = await RunAsync(app, "reminder", "help");

        Assert.Contains(
            "Cancel a reminder (deletes a one-shot; disables a recurring reminder)",
            result.Stdout,
            StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        DaemonHost host, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await ReminderCommand.RunAsync(args, host.Api, stdout, stderr);
        return (exitCode, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd());
    }

    private async Task<DaemonHost> StartDaemonAsync()
    {
        var actor = _actorSystem.ActorOf(Props.Create(() => new CannedManager(_answers)));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new SchedulingConfig { Enabled = true });
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddSingleton<IRequiredActor<ReminderManagerActorKey>>(new FixedActor(actor));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapReminderEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);

        var api = new DaemonApi(
            new FixedHttpClientFactory(app.GetTestClient),
            new ConfigurationBuilder().Build(),
            new NetclawPaths(_dir.Path),
            DaemonApiTestDependencies.CreateProofProtector(),
            TimeProvider.System);
        return new DaemonHost(app, api);
    }

    private sealed class DaemonHost(WebApplication app, DaemonApi api) : IAsyncDisposable
    {
        public DaemonApi Api { get; } = api;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private sealed class CannedAnswers
    {
        public GetReminderResponse? Get { get; set; }
        public ReminderStatusResponse? Status { get; set; }
        public ReminderCancelledResponse? Cancel { get; set; }
    }

    private sealed class CannedManager : ReceiveActor
    {
        public CannedManager(CannedAnswers answers)
        {
            Receive<GetReminderCommand>(_ => Sender.Tell(answers.Get ?? new GetReminderResponse(null)));
            Receive<GetReminderStatusQuery>(_ => Sender.Tell(answers.Status!));
            Receive<CancelReminderCommand>(command =>
                Sender.Tell(answers.Cancel ?? new ReminderCancelledResponse(command.Id, Found: false)));
        }
    }

    private sealed class FixedActor(IActorRef actor) : IRequiredActor<ReminderManagerActorKey>
    {
        public IActorRef ActorRef => actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(actor);
    }

    private sealed class FixedHttpClientFactory(Func<HttpClient> create) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => create();
    }
}
