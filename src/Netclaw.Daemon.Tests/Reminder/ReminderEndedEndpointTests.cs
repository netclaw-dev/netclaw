// -----------------------------------------------------------------------
// <copyright file="ReminderEndedEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Daemon.Reminders;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Daemon.Tests.Reminder;

/// <summary>
/// How the reminder routes answer for an ended one-shot (change <c>oneshot-reminder-autodelete</c>,
/// design D7). The reminder manager is a stand-in that replies with canned protocol answers, so these
/// tests cover the mapping from the answer to the HTTP status and the JSON body.
/// </summary>
public sealed class ReminderEndedEndpointTests : IAsyncDisposable
{
    private const string Prompt = "Check PR 42 and tell me when CI is green.";
    private static readonly DateTimeOffset EndedAt = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PurgeAt = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    private readonly ActorSystem _actorSystem = ActorSystem.Create($"reminder-ended-endpoints-{Guid.NewGuid():N}");
    private readonly CannedAnswers _answers = new();

    public async ValueTask DisposeAsync() => await _actorSystem.Terminate();

    private static object Ended(string outcome, string? lastError = null, string? instructions = null)
        => ReminderEndedProtocol.NewEndedInfo(outcome, EndedAt, PurgeAt, lastError, instructions);

    private static object Build(Type type, params (string Name, object? Value)[] values)
        => ReminderEndedProtocol.Construct(type, values.ToDictionary(v => v.Name, v => v.Value));

    private static ReminderStatusResponse EndedStatus(object? ended)
        => (ReminderStatusResponse)Build(
            typeof(ReminderStatusResponse),
            ("Id", new ReminderId("check-pr-42")),
            ("Found", true),
            ("Enabled", false),
            ("Executing", false),
            ("ConsecutiveFailures", 0),
            ("SkippedDuplicates", 0),
            ("RecentHistory", (IReadOnlyList<HistoryRecord>)[]),
            ("Ended", ended));

    private static GetReminderResponse EndedGet(object? ended)
        => (GetReminderResponse)Build(typeof(GetReminderResponse), ("Reminder", null), ("Ended", ended));

    private static ReminderCancelledResponse Cancelled(
        bool found, object? alreadyEnded = null, bool deleted = false)
    {
        // Pass only what the case needs, so a case that needs no new member does not depend on one.
        var named = new Dictionary<string, object?> { ["Id"] = new ReminderId("check-pr-42"), ["Found"] = found };
        if (alreadyEnded is not null)
            named["AlreadyEnded"] = alreadyEnded;
        if (deleted)
            named["Deleted"] = true;
        return (ReminderCancelledResponse)ReminderEndedProtocol.Construct(typeof(ReminderCancelledResponse), named);
    }

    private static string Property(JsonElement element, string name)
        => element.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value.ToString();

    // ── GET /api/reminders/{id} ──

    [Fact]
    public async Task Get_of_an_ended_oneshot_is_404_and_names_the_outcome_the_end_time_and_the_purge_time()
    {
        _answers.Get = EndedGet(Ended("Completed"));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(
            "/api/reminders/check-pr-42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var message = Property(JsonDocument.Parse(body).RootElement, "error");
        Assert.Contains("ended (completed) at 2026-10-16 10:00:00Z", message, StringComparison.Ordinal);
        Assert.Contains("2026-10-28 10:00:00Z", message, StringComparison.Ordinal);
        Assert.Contains("netclaw reminder history check-pr-42", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_of_an_ended_failed_oneshot_does_not_show_the_prompt()
    {
        _answers.Get = EndedGet(Ended("Failed", "boom", Prompt));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(
            "/api/reminders/check-pr-42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(
            Prompt, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    // ── GET /api/reminders/{id}/status ──

    [Fact]
    public async Task Status_of_an_ended_failed_oneshot_reports_the_end_and_the_prompt()
    {
        _answers.Status = EndedStatus(Ended("Failed", "model endpoint is not available", Prompt));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(
            "/api/reminders/check-pr-42/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var ended = json.EnumerateObject().Single(p => string.Equals(p.Name, "ended", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Equal("Failed", Property(ended, "outcome"));
        Assert.Equal(EndedAt, DateTimeOffset.Parse(Property(ended, "endedAt"), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime());
        Assert.Equal(PurgeAt, DateTimeOffset.Parse(Property(ended, "purgeAt"), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime());
        Assert.Equal("model endpoint is not available", Property(ended, "lastError"));
        Assert.Equal(Prompt, Property(ended, "instructions"));
    }

    [Fact]
    public async Task Status_of_an_ended_completed_oneshot_carries_no_prompt()
    {
        _answers.Status = EndedStatus(Ended("Completed"));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(
            "/api/reminders/check-pr-42/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(body).RootElement;
        var ended = json.EnumerateObject().Single(p => string.Equals(p.Name, "ended", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Equal("Completed", Property(ended, "outcome"));
        Assert.True(
            !ended.EnumerateObject().Any(p => string.Equals(p.Name, "instructions", StringComparison.OrdinalIgnoreCase))
            || ended.EnumerateObject().Single(p => string.Equals(p.Name, "instructions", StringComparison.OrdinalIgnoreCase)).Value.ValueKind == JsonValueKind.Null);
    }

    // ── DELETE /api/reminders/{id} (cancel) ──

    [Fact]
    public async Task Cancel_that_ended_a_oneshot_says_cancelled_and_deleted()
    {
        _answers.Cancel = Cancelled(found: true, deleted: true);
        await using var app = await StartAsync();

        var response = await app.GetTestClient().DeleteAsync(
            "/api/reminders/check-pr-42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Property(JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement, "message");
        Assert.Equal("Reminder 'check-pr-42' cancelled and deleted.", message);
    }

    [Fact]
    public async Task Cancel_of_a_recurring_reminder_says_cancelled_disabled()
    {
        _answers.Cancel = Cancelled(found: true);
        await using var app = await StartAsync();

        var response = await app.GetTestClient().DeleteAsync(
            "/api/reminders/check-pr-42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Property(JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement, "message");
        Assert.Equal("Reminder 'check-pr-42' cancelled (disabled).", message);
    }

    [Fact]
    public async Task Cancel_of_an_ended_oneshot_is_200_and_says_that_it_already_ended()
    {
        _answers.Cancel = Cancelled(found: true, alreadyEnded: Ended("Completed"));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().DeleteAsync(
            "/api/reminders/check-pr-42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Property(JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement, "message");
        Assert.Contains("already ended (completed at 2026-10-16 10:00:00Z)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_of_an_unknown_reminder_is_404()
    {
        _answers.Cancel = Cancelled(found: false);
        await using var app = await StartAsync();

        var response = await app.GetTestClient().DeleteAsync(
            "/api/reminders/check-pr-42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Harness ──

    private async Task<WebApplication> StartAsync()
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
        return app;
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
            Receive<CancelReminderCommand>(command => Sender.Tell(answers.Cancel ?? new ReminderCancelledResponse(command.Id, Found: false)));
        }
    }

    private sealed class FixedActor(IActorRef actor) : IRequiredActor<ReminderManagerActorKey>
    {
        public IActorRef ActorRef => actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(actor);
    }
}
