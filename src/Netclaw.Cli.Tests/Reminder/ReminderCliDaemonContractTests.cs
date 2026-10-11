// -----------------------------------------------------------------------
// <copyright file="ReminderCliDaemonContractTests.cs" company="Petabridge, LLC">
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
/// Drives <c>netclaw reminder import|show|history</c> through the real
/// <see cref="DaemonApi"/> and the real reminder REST endpoints on a test host.
/// Only the reminder manager actor is replaced, so both JSON serializers under
/// test are the production ones.
/// </summary>
public sealed class ReminderCliDaemonContractTests : IAsyncDisposable
{
    private const string NamedEnumDefinition = """
        {
          "id": "import-e2e",
          "title": "Import end to end",
          "schedule": { "type": "Cron", "cronExpression": "0 8 * * *", "originalExpression": "0 8 * * *" },
          "instructions": "Check the build",
          "delivery": { "kind": "None" },
          "audience": "Personal",
          "boundary": "personal",
          "createdBy": "cli"
        }
        """;

    private readonly DisposableTempDir _dir = new();
    private readonly ActorSystem _actorSystem = ActorSystem.Create($"reminder-cli-contract-{Guid.NewGuid():N}");
    private readonly InMemoryReminderManager _manager = new();

    public async ValueTask DisposeAsync()
    {
        await _actorSystem.Terminate();
        _dir.Dispose();
    }

    [Fact]
    public async Task Import_definition_with_enum_names_creates_the_reminder()
    {
        await using var app = await StartDaemonAsync();
        var file = WriteFile("definition.json", NamedEnumDefinition);

        var result = await RunAsync(app, "reminder", "import", file);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("Imported reminder 'import-e2e'.", result.Stdout, StringComparison.Ordinal);
        var saved = Assert.Single(_manager.Saved.Values);
        Assert.Equal(ReminderScheduleType.Cron, saved.Schedule.Type);
        Assert.Equal(DeliveryKind.None, saved.Delivery.Kind);
        Assert.Equal(TrustAudience.Personal, saved.Audience);
    }

    [Fact]
    public async Task Import_rejected_by_the_daemon_reports_the_daemon_answer()
    {
        await using var app = await StartDaemonAsync();
        var file = WriteFile("definition.json", NamedEnumDefinition);
        await ImportAsync(app, file);

        var duplicate = await RunAsync(app, "reminder", "import", file);

        Assert.Equal(1, duplicate.ExitCode);
        Assert.Contains("[FAIL] Reminder 'import-e2e' already exists.", duplicate.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("unable to reach daemon", duplicate.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("validate")]
    public async Task Cron_with_an_unknown_zone_prefix_reports_the_zone_not_a_bad_expression(string subcommand)
    {
        await using var app = await StartDaemonAsync();
        var file = WriteFile("definition.json", NamedEnumDefinition.Replace(
            "\"cronExpression\": \"0 8 * * *\"", "\"cronExpression\": \"CRON_TZ=Not/AZone 0 8 * * *\"", StringComparison.Ordinal));

        var result = await RunAsync(app, "reminder", subcommand, file);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown time zone 'Not/AZone'.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Cron expression is invalid.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Daemon_reply_with_an_empty_body_is_not_reported_as_an_unreachable_daemon()
    {
        var file = WriteFile("definition.json", NamedEnumDefinition);
        var api = new DaemonApi(
            new StubHttpClientFactory(new Uri("http://daemon.test"), HttpStatusCode.BadRequest),
            new ConfigurationBuilder().Build(),
            new NetclawPaths(_dir.Path));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReminderCommand.RunAsync(["reminder", "import", file], api, stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("[FAIL] daemon returned 400 Bad Request", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unable to reach daemon", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_prints_the_fire_time_in_the_first_column()
    {
        await using var app = await StartDaemonAsync();
        var file = WriteFile("definition.json", NamedEnumDefinition);
        await ImportAsync(app, file);
        _manager.History.Add(new HistoryRecord(
            new DateTimeOffset(2026, 10, 7, 8, 0, 3, TimeSpan.Zero), true, 1520, "session-1", null));

        var result = await RunAsync(app, "reminder", "history", "import-e2e");

        Assert.True(result.ExitCode == 0, result.Stderr);
        var row = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.StartsWith("2026-10-07 08:00:03Z", row, StringComparison.Ordinal);
    }

    [Fact]
    public void History_row_aligns_every_column()
    {
        var row = ReminderCommand.FormatHistoryRow(new HistoryRecord(
            new DateTimeOffset(2026, 10, 7, 8, 0, 3, TimeSpan.Zero), false, 42, "session-1", "boom"));

        Assert.Equal("2026-10-07 08:00:03Z       failed    42            session-1", row);
        Assert.Equal(
            ReminderCommand.HistoryHeader.IndexOf("session_id", StringComparison.Ordinal),
            row.IndexOf("session-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_prints_a_table_with_one_row_per_reminder()
    {
        await using var app = await StartDaemonAsync();
        await ImportAsync(app, WriteFile("definition.json", NamedEnumDefinition));
        _manager.Saved[new ReminderId("a-much-longer-reminder-id")] = _manager.Saved[new ReminderId("import-e2e")] with
        {
            Id = new ReminderId("a-much-longer-reminder-id"),
            Title = "Failed one",
            Enabled = false,
            ConsecutiveFailures = 5,
            TerminalOutcome = ReminderTerminalOutcome.Failed
        };

        var result = await RunAsync(app, "reminder", "list");

        Assert.True(result.ExitCode == 0, result.Stderr);
        var lines = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("id ", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("title", lines[0], StringComparison.Ordinal);
        var imported = lines.Single(l => l.StartsWith("import-e2e", StringComparison.Ordinal));
        var failed = lines.Single(l => l.StartsWith("a-much-longer-reminder-id", StringComparison.Ordinal));
        Assert.Contains("enabled", imported, StringComparison.Ordinal);
        Assert.EndsWith("Import end to end", imported, StringComparison.Ordinal);
        Assert.Contains("failed", failed, StringComparison.Ordinal);
        // Columns are sized from the longest id, so every row's status column starts at the same offset.
        Assert.Equal(
            lines[0].IndexOf("status", StringComparison.Ordinal),
            imported.IndexOf("enabled", StringComparison.Ordinal));
        Assert.Equal(
            lines[0].IndexOf("status", StringComparison.Ordinal),
            failed.IndexOf("failed", StringComparison.Ordinal));
        Assert.DoesNotContain("\"id\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_json_prints_exactly_what_the_command_printed_before_the_table()
    {
        await using var app = await StartDaemonAsync();
        await ImportAsync(app, WriteFile("definition.json", NamedEnumDefinition));

        var result = await RunAsync(app, "reminder", "list", "--json");

        using var raw = await app.Api.ListRemindersAsync(TestContext.Current.CancellationToken);
        var body = await raw.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var previousOutput = System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(previousOutput, result.Stdout);
        Assert.StartsWith("[", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_table_shows_the_utc_stamp_not_the_sentence_the_daemon_formats()
    {
        await using var app = await StartDaemonAsync();
        await ImportAsync(app, WriteFile("definition.json", NamedEnumDefinition));

        var result = await RunAsync(app, "reminder", "list");

        var row = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.StartsWith("import-e2e", StringComparison.Ordinal));
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z", row);
        Assert.DoesNotContain("(", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--jsno")]
    [InlineData("extra")]
    public async Task List_rejects_unknown_options_without_calling_the_daemon(string option)
    {
        await using var app = await StartDaemonAsync();

        var result = await RunAsync(app, "reminder", "list", option);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Unknown option '{option}'", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("", result.Stdout);
    }

    [Theory]
    [InlineData(false, "No reminders.")]
    [InlineData(true, "No active reminders.")]
    public async Task List_with_no_reminders_exits_zero_and_says_so(bool json, string expected)
    {
        await using var app = await StartDaemonAsync();

        var result = json
            ? await RunAsync(app, "reminder", "list", "--json")
            : await RunAsync(app, "reminder", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Stdout);
    }

    [Fact]
    public void History_row_shows_a_denied_run_as_denied()
    {
        var row = ReminderCommand.FormatHistoryRow(new HistoryRecord(
            new DateTimeOffset(2026, 10, 7, 8, 0, 3, TimeSpan.Zero), false, 42, "session-1", "Tool call denied (shell_execute): x", ToolDenied: true));

        Assert.Equal("2026-10-07 08:00:03Z       denied    42            session-1", row);
    }

    private static async Task ImportAsync(DaemonHost host, string file)
    {
        var result = await RunAsync(host, "reminder", "import", file);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        DaemonHost host,
        params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await ReminderCommand.RunAsync(args, host.Api, stdout, stderr);
        return (exitCode, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd());
    }

    private async Task<DaemonHost> StartDaemonAsync()
    {
        var actor = _actorSystem.ActorOf(Props.Create(() => new ManagerActor(_manager)));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new SchedulingConfig { Enabled = true });
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddSingleton<IRequiredActor<ReminderManagerActorKey>>(new StubRequiredActor(actor));

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
            new StubHttpClientFactory(app.GetTestClient),
            new ConfigurationBuilder().Build(),
            new NetclawPaths(_dir.Path));
        return new DaemonHost(app, api);
    }

    private sealed class DaemonHost(WebApplication app, DaemonApi api) : IAsyncDisposable
    {
        public DaemonApi Api { get; } = api;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    /// <summary>State behind the stand-in for <c>ReminderManagerActor</c>.</summary>
    private sealed class InMemoryReminderManager
    {
        public Dictionary<ReminderId, ReminderDefinition> Saved { get; } = [];

        public List<HistoryRecord> History { get; } = [];
    }

    private sealed class ManagerActor : ReceiveActor
    {
        public ManagerActor(InMemoryReminderManager state)
        {
            Receive<SaveReminderCommand>(cmd =>
            {
                var definition = cmd.Definition;
                var exists = state.Saved.ContainsKey(definition.Id);
                if (exists && cmd.WriteMode is ReminderWriteMode.CreateOnly)
                {
                    Sender.Tell(new ReminderSavedResponse(
                        definition.Id, definition.Title, false, null,
                        ReminderSaveError.Conflict, $"Reminder '{definition.Id.Value}' already exists."));
                    return;
                }

                state.Saved[definition.Id] = definition;
                Sender.Tell(new ReminderSavedResponse(definition.Id, definition.Title, true, null));
            });

            Receive<GetReminderCommand>(cmd =>
            {
                Sender.Tell(new GetReminderResponse(state.Saved.TryGetValue(cmd.Id, out var d)
                    ? new ReminderInfo(
                        d.Id, d.Title, d.Instructions, d.Delivery, d.DeliveryRequired, d.DeliveryInstructions,
                        d.Schedule, null, d.Enabled, null, d.Audience)
                    : null));
            });

            Receive<ListRemindersCommand>(_ =>
                Sender.Tell(new ReminderListResponse(state.Saved.Values.Select(d => new ReminderInfo(
                    d.Id, d.Title, d.Instructions, d.Delivery, d.DeliveryRequired, d.DeliveryInstructions,
                    d.Schedule, new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero), d.Enabled, null, d.Audience,
                    ConsecutiveFailures: d.ConsecutiveFailures, TerminalOutcome: d.TerminalOutcome)).ToList())));

            Receive<GetReminderHistoryQuery>(query =>
                Sender.Tell(new ReminderHistoryResponse(query.Id, state.Saved.ContainsKey(query.Id), state.History)));
        }
    }

    private sealed class StubRequiredActor(IActorRef actor) : IRequiredActor<ReminderManagerActorKey>
    {
        public IActorRef ActorRef => actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(actor);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpClient> _create;

        public StubHttpClientFactory(Func<HttpClient> create) => _create = create;

        public StubHttpClientFactory(Uri baseAddress, HttpStatusCode emptyReplyStatus)
            => _create = () => new HttpClient(new EmptyReplyHandler(emptyReplyStatus)) { BaseAddress = baseAddress };

        public HttpClient CreateClient(string name) => _create();
    }

    private sealed class EmptyReplyHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
