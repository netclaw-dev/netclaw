using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Reminder;

public sealed class ReminderEndpointAuthorizationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private readonly ReminderDefinitionStore _definitionStore;
    private readonly ReminderHistoryStore _historyStore;

    public ReminderEndpointAuthorizationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-endpoint-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));

        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();
        _definitionStore = new ReminderDefinitionStore(paths);
        _historyStore = new ReminderHistoryStore(paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_definitionStore);
        builder.Services.AddSingleton(_historyStore);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new EffectivePolicyDefaults(
            DeploymentPosture.Team,
            TrustAudience.Team,
            ShellExecutionMode.Off,
            UsedStrictFallback: false));
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddNetclawAuthSchemes();
        builder.Services.AddAuthorization();

        var app = builder.Build();

        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();

        var reminders = app.MapGroup("/api/reminders").RequireAuthorization();

        reminders.MapPost("", async (
            ReminderCreateRequest request,
            ReminderDefinitionStore definitionStore,
            ClaimsPrincipalMapper mapper,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var identity = mapper.Map(httpContext.User);
            if (identity.Principal is not PrincipalClassification.Operator)
                return Results.BadRequest(new { error = "Reminder audience authorization context is required." });

            var parsedAudience = default(TrustAudience);
            if (!string.IsNullOrWhiteSpace(request.Audience)
                && !SecurityPolicyDefaults.TryParseAudience(request.Audience, out parsedAudience))
            {
                return Results.BadRequest(new { error = $"Error: Invalid audience '{request.Audience}'. Use 'personal', 'team', or 'public'." });
            }

            var effectiveAudience = string.IsNullOrWhiteSpace(request.Audience)
                ? TrustAudience.Personal
                : parsedAudience;

            var now = _timeProvider.GetUtcNow();
            var definition = new ReminderDefinition
            {
                Id = request.Id,
                Title = request.Name,
                Instructions = request.Prompt,
                Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
                DeliveryInstructions = request.NotifyInstructions ?? "Reply in thread.",
                Schedule = new ReminderSchedule
                {
                    Type = ReminderScheduleType.OneShot,
                    FireAt = now.AddMinutes(30)
                },
                Audience = effectiveAudience,
                Enabled = true,
                CreatedBy = "test",
                CreatedAt = now,
                UpdatedAt = now
            };

            definitionStore.Save(definition);
            return Results.Ok(new { message = $"Reminder '{request.Name}' scheduled." });
        });

        reminders.MapPost("/import", async (
            ReminderImportRequest request,
            ReminderDefinitionStore definitionStore,
            ClaimsPrincipalMapper mapper,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (request.Definition is null)
                return Results.BadRequest(new { error = "Reminder definition is required." });

            var identity = mapper.Map(httpContext.User);
            if (identity.Principal is not PrincipalClassification.Operator)
            {
                return Results.BadRequest(new
                {
                    error = "Reminder audience authorization context is required.",
                    code = ReminderSaveError.Validation.ToString(),
                    id = request.Definition.Id
                });
            }

            if (request.Definition.Audience is not { } audience)
            {
                return Results.BadRequest(new
                {
                    error = "Reminder definition must include a valid audience for import.",
                    code = ReminderSaveError.Validation.ToString(),
                    id = request.Definition.Id
                });
            }

            if (audience > TrustAudience.Personal)
            {
                return Results.BadRequest(new
                {
                    error = $"Requested audience '{audience.ToWireValue()}' exceeds creator authority 'Operator/LocalProcess' (personal).",
                    code = ReminderSaveError.Validation.ToString(),
                    id = request.Definition.Id
                });
            }

            definitionStore.Save(request.Definition);
            return Results.Ok(new { id = request.Definition.Id, message = $"Imported reminder '{request.Definition.Id}'." });
        });

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Create_persists_personal_audience_when_omitted_for_loopback_operator()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "rest-create-inherit",
            name = "rest-create-inherit",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TrustAudience.Personal, _definitionStore.Get(new ReminderId("rest-create-inherit"))!.Audience);
    }

    [Fact]
    public async Task Create_rejects_invalid_audience_without_persisting()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "rest-create-invalid",
            name = "rest-create-invalid",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m",
            audience = "superuser"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid audience", body.GetProperty("error").GetString());
        Assert.Null(_definitionStore.Get(new ReminderId("rest-create-invalid")));
    }

    [Fact]
    public async Task Import_rejects_missing_audience_without_persisting()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        var now = _timeProvider.GetUtcNow();

        var response = await client.PostAsJsonAsync("/api/reminders/import", new ReminderImportRequest(
            new ReminderDefinition
            {
                Id = "rest-import-missing-audience",
                Title = "rest-import-missing-audience",
                Instructions = "check status",
                Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
                DeliveryInstructions = "reply",
                Schedule = new ReminderSchedule
                {
                    Type = ReminderScheduleType.OneShot,
                    FireAt = now.AddMinutes(30)
                },
                Enabled = true,
                CreatedBy = "test",
                CreatedAt = now,
                UpdatedAt = now
            }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("must include a valid audience", body.GetProperty("error").GetString());
        Assert.Null(_definitionStore.Get(new ReminderId("rest-import-missing-audience")));
    }

    [Fact]
    public async Task Create_requires_authenticated_authority_context()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "rest-create-unauthorized",
            name = "rest-create-unauthorized",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_definitionStore.Get(new ReminderId("rest-create-unauthorized")));
    }

    private sealed record ReminderCreateRequest(
        string Id,
        string Name,
        string Prompt,
        string ScheduleType,
        string Schedule,
        string? Audience = null,
        string? NotifyInstructions = null);

    private sealed record ReminderImportRequest(ReminderDefinition Definition);
}
