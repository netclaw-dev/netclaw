// -----------------------------------------------------------------------
// <copyright file="ReminderEndpointAuthorizationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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

public sealed class ReminderEndpointAuthorizationTests : IAsyncDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _timeProvider;
    private readonly ReminderDefinitionStore _definitionStore;
    private readonly ReminderHistoryStore _historyStore;
    private readonly ActorSystem _actorSystem;
    private readonly TestReminderActor _testActor;
    private readonly IActorRef _actorRef;

    public ReminderEndpointAuthorizationTests()
    {
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 9, 12, 0, 0, TimeSpan.Zero));

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _definitionStore = new ReminderDefinitionStore(paths);
        _historyStore = new ReminderHistoryStore(paths);

        _actorSystem = ActorSystem.Create($"reminder-endpoint-tests-{Guid.NewGuid():N}");
        _testActor = new TestReminderActor();
        _actorRef = _actorSystem.ActorOf(Props.Create(() => new RecordingReminderActor(_testActor)));
    }

    public async ValueTask DisposeAsync()
    {
        await _actorSystem.Terminate();
        _dir.Dispose();
    }

    // ── Test case 1: regression — authenticated non-Operator is rejected with 403 ──

    [Fact]
    public async Task NonOperator_POST_reminders_returns_403_and_actor_receives_no_command()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false, addNonOperatorScheme: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(NonOperatorAuthHandler.HeaderName, NonOperatorAuthHandler.HeaderValue);

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "regression-non-operator",
            name = "regression-non-operator",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The 403 guard fires before any actor interaction
        Assert.Empty(_testActor.ReceivedMessages);
    }

    // ── Test case 2: golden path — Operator POST creates a reminder ──

    [Fact]
    public async Task Operator_POST_reminders_succeeds_and_actor_receives_SaveReminderCommand_with_source_audience()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "golden-path-create",
            name = "golden-path-create",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m",
            deliveryKind = "none"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saveCmd = _testActor.ReceivedMessages.OfType<SaveReminderCommand>().FirstOrDefault();
        Assert.NotNull(saveCmd);
        Assert.NotNull(saveCmd.Authorization?.SourceAudience);
    }

    // ── Test case 3: unauthenticated POST → 401 ──

    [Fact]
    public async Task Unauthenticated_POST_reminders_returns_401()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "unauthenticated-create",
            name = "unauthenticated-create",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_testActor.ReceivedMessages);
    }

    // ── Test case 4: POST with invalid audience value → 400, no command dispatched ──

    [Fact]
    public async Task POST_reminders_with_invalid_audience_returns_400_and_no_command_dispatched()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "invalid-audience",
            name = "invalid-audience",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m",
            deliveryKind = "none",
            audience = "superuser"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid audience", body.GetProperty("error").GetString());
        // Audience is validated inside SetReminderTool — the actor receives the command
        // only after the tool validates params. If the tool returns an error, there is no command.
        // (The tool emits "Error: Invalid audience..." before Ask, so no SaveReminderCommand is sent.)
        Assert.DoesNotContain(_testActor.ReceivedMessages, m => m is SaveReminderCommand { } cmd
            && cmd.Definition.Id.Value == "invalid-audience");
    }

    // ── Test case 5a: import as non-Operator → 400 with validation error ──

    [Fact]
    public async Task NonOperator_POST_reminders_import_actor_returns_validation_error()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false, addNonOperatorScheme: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(NonOperatorAuthHandler.HeaderName, NonOperatorAuthHandler.HeaderValue);

        var now = _timeProvider.GetUtcNow();
        var response = await client.PostAsJsonAsync("/api/reminders/import", new
        {
            definition = new
            {
                id = "import-non-operator",
                title = "import-non-operator",
                instructions = "check status",
                delivery = new { kind = 2 }, // None
                deliveryInstructions = "reply",
                schedule = new
                {
                    type = 0,
                    fireAtMs = now.AddMinutes(30).ToUnixTimeMilliseconds()
                },
                audience = 0, // Personal
                boundary = "personal",
                enabled = true,
                createdBy = "test",
                createdAtMs = now.ToUnixTimeMilliseconds(),
                updatedAtMs = now.ToUnixTimeMilliseconds()
            }
        }, TestContext.Current.CancellationToken);

        // Non-Operator: authorization is null → actor receives SaveReminderCommand with null authorization
        // The actor's validator logic in ReminderManagerActor will return a validation error
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Test case 5b: import as Operator → success ──

    [Fact]
    public async Task Operator_POST_reminders_import_succeeds()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var now = _timeProvider.GetUtcNow();
        var response = await client.PostAsJsonAsync("/api/reminders/import", new
        {
            definition = new
            {
                id = "import-operator",
                title = "import-operator",
                instructions = "check status",
                delivery = new { kind = 2 }, // None
                deliveryInstructions = "reply",
                schedule = new
                {
                    type = 0,
                    fireAtMs = now.AddMinutes(30).ToUnixTimeMilliseconds()
                },
                audience = 0, // Personal
                boundary = "personal",
                enabled = true,
                createdBy = "test",
                createdAtMs = now.ToUnixTimeMilliseconds(),
                updatedAtMs = now.ToUnixTimeMilliseconds()
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saveCmd = _testActor.ReceivedMessages.OfType<SaveReminderCommand>().FirstOrDefault();
        Assert.NotNull(saveCmd);
        Assert.Equal("import-operator", saveCmd.Definition.Id.Value);
    }

    // ── Test case 6: DELETE with ?permanent=true → DeleteReminderCommand; without → CancelReminderCommand ──

    [Fact]
    public async Task DELETE_with_permanent_true_sends_DeleteReminderCommand()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/reminders/some-id?permanent=true",
            TestContext.Current.CancellationToken);

        // NotFound because the test actor returns Found=false, but the command type should be DeleteReminderCommand
        var deleteCmd = _testActor.ReceivedMessages.OfType<DeleteReminderCommand>().FirstOrDefault();
        Assert.NotNull(deleteCmd);
        Assert.Equal("some-id", deleteCmd.Id.Value);
        Assert.DoesNotContain(_testActor.ReceivedMessages, m => m is CancelReminderCommand);
    }

    [Fact]
    public async Task DELETE_without_permanent_sends_CancelReminderCommand()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/reminders/some-id",
            TestContext.Current.CancellationToken);

        var cancelCmd = _testActor.ReceivedMessages.OfType<CancelReminderCommand>().FirstOrDefault();
        Assert.NotNull(cancelCmd);
        Assert.Equal("some-id", cancelCmd.Id.Value);
        Assert.DoesNotContain(_testActor.ReceivedMessages, m => m is DeleteReminderCommand);
    }

    // ── Re-expressed original tests against the real endpoints ──

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
            schedule = "30m",
            deliveryKind = "none"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saveCmd = _testActor.ReceivedMessages.OfType<SaveReminderCommand>().FirstOrDefault();
        Assert.NotNull(saveCmd);
        // Operator loopback → authorization carries Personal audience
        Assert.Equal(TrustAudience.Personal, saveCmd.Authorization?.SourceAudience);
        Assert.Equal(TrustBoundary.TrustedInstance, saveCmd.Definition.Boundary);
    }

    [Fact]
    public async Task Create_downscoped_public_audience_rewrites_boundary_to_public()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/reminders", new
        {
            id = "rest-create-public-boundary",
            name = "rest-create-public-boundary",
            prompt = "check status",
            scheduleType = "once",
            schedule = "30m",
            deliveryKind = "none",
            audience = "public"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saveCmd = _testActor.ReceivedMessages.OfType<SaveReminderCommand>()
            .FirstOrDefault(x => x.Definition.Id.Value == "rest-create-public-boundary");
        Assert.NotNull(saveCmd);
        Assert.Equal(TrustAudience.Public, saveCmd.Definition.Audience);
        Assert.Equal(TrustBoundary.Public, saveCmd.Definition.Boundary);
    }

    [Fact]
    public async Task Create_rejects_invalid_audience_without_dispatching_command()
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
            deliveryKind = "none",
            audience = "superuser"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid audience", body.GetProperty("error").GetString());
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
        Assert.Empty(_testActor.ReceivedMessages);
    }

    [Fact]
    public async Task Import_requires_authenticated_authority_context()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();
        var now = _timeProvider.GetUtcNow();

        var response = await client.PostAsJsonAsync("/api/reminders/import", new
        {
            definition = new
            {
                id = "rest-import-unauthorized",
                title = "rest-import-unauthorized",
                instructions = "check status",
                delivery = new { kind = 2 }, // None
                deliveryInstructions = "reply",
                schedule = new
                {
                    type = 0,
                    fireAtMs = now.AddMinutes(30).ToUnixTimeMilliseconds()
                },
                audience = 0, // Personal
                boundary = "personal",
                enabled = true,
                createdBy = "test",
                createdAtMs = now.ToUnixTimeMilliseconds(),
                updatedAtMs = now.ToUnixTimeMilliseconds()
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_testActor.ReceivedMessages);
    }

    // ── App factory ──

    private async Task<WebApplication> CreateAppAsync(
        bool spoofLoopback,
        bool addNonOperatorScheme = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_definitionStore);
        builder.Services.AddSingleton(_historyStore);
        builder.Services.AddSingleton<TimeProvider>(_timeProvider);
        builder.Services.AddSingleton(new SchedulingConfig { Enabled = true });
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();

        // Wire the test actor as IRequiredActor<ReminderManagerActorKey>
        builder.Services.AddSingleton<IRequiredActor<ReminderManagerActorKey>>(
            new FakeRequiredActor(_actorRef));

        if (addNonOperatorScheme)
        {
            // Non-Operator takes priority when the special header is present;
            // fall back to Loopback scheme otherwise.
            builder.Services
                .AddAuthentication("TestAuthSelector")
                .AddPolicyScheme("TestAuthSelector", "non-operator or loopback", options =>
                {
                    options.ForwardDefaultSelector = ctx =>
                        ctx.Request.Headers.ContainsKey(NonOperatorAuthHandler.HeaderName)
                            ? NonOperatorAuthHandler.SchemeName
                            : LoopbackAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, NonOperatorAuthHandler>(
                    NonOperatorAuthHandler.SchemeName, _ => { })
                .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                    LoopbackAuthenticationHandler.SchemeName, _ => { });
            builder.Services.AddSingleton(new DaemonConfig());
        }
        else
        {
            builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        }

        builder.Services.AddAuthorization();
        builder.Services.AddLogging();

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
        app.MapReminderEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    // ── Fakes and helpers ──

    /// <summary>
    /// Mutable sink that records all messages the test actor receives.
    /// Accessed from the test thread after awaiting the HTTP response, so no
    /// concurrent access — the actor Tell completes before the endpoint returns.
    /// </summary>
    private sealed class TestReminderActor
    {
        private readonly List<object> _received = [];
        public IReadOnlyList<object> ReceivedMessages => _received;

        public void Record(object message) => _received.Add(message);
    }

    /// <summary>
    /// ReceiveActor that records every command it handles, replies with
    /// canned successes, and delegates recording to <see cref="TestReminderActor"/>.
    /// </summary>
    private sealed class RecordingReminderActor : ReceiveActor
    {
        public RecordingReminderActor(TestReminderActor sink)
        {
            Receive<SaveReminderCommand>(cmd =>
            {
                sink.Record(cmd);
                // Null authorization means non-Operator called import — reply with validation error
                // so the endpoint returns 400 (mirrors what ReminderManagerActor would do).
                if (cmd.Authorization?.SourceAudience is null)
                {
                    Sender.Tell(new ReminderSavedResponse(
                        cmd.Definition.Id,
                        cmd.Definition.Title,
                        Success: false,
                        NextFire: null,
                        Error: ReminderSaveError.Validation,
                        ErrorMessage: "Reminder audience authorization context is required."));
                }
                else
                {
                    Sender.Tell(new ReminderSavedResponse(
                        cmd.Definition.Id,
                        cmd.Definition.Title,
                        Success: true,
                        NextFire: DateTimeOffset.UtcNow.AddMinutes(30),
                        Error: ReminderSaveError.None));
                }
            });

            Receive<ListRemindersCommand>(cmd =>
            {
                sink.Record(cmd);
                Sender.Tell(new ReminderListResponse([]));
            });

            Receive<DeleteReminderCommand>(cmd =>
            {
                sink.Record(cmd);
                Sender.Tell(new ReminderDeletedResponse(cmd.Id, Found: false));
            });

            Receive<CancelReminderCommand>(cmd =>
            {
                sink.Record(cmd);
                Sender.Tell(new ReminderCancelledResponse(cmd.Id, Found: false));
            });

            Receive<GetReminderCommand>(cmd =>
            {
                sink.Record(cmd);
                Sender.Tell(new GetReminderResponse(null));
            });

            Receive<DisableReminderCommand>(cmd =>
            {
                sink.Record(cmd);
                Sender.Tell(new ReminderStateResponse(cmd.Id, Found: false, Enabled: false));
            });

            Receive<EnableReminderCommand>(cmd =>
            {
                sink.Record(cmd);
                Sender.Tell(new ReminderStateResponse(cmd.Id, Found: false, Enabled: false));
            });
        }
    }

    /// <summary>
    /// Wraps a real <see cref="IActorRef"/> as <see cref="IRequiredActor{T}"/> so it
    /// can be injected into the test host's DI container.
    /// </summary>
    private sealed class FakeRequiredActor(IActorRef actorRef) : IRequiredActor<ReminderManagerActorKey>
    {
        public IActorRef ActorRef => actorRef;
        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(actorRef);
    }

    /// <summary>
    /// Test authentication handler that authenticates every request carrying the
    /// <c>X-Test-NonOperator</c> header as an authenticated but non-Operator principal.
    ///
    /// The handler deliberately omits the <c>netclaw:principal</c> claim (used by
    /// <see cref="ClaimsPrincipalMapper"/> to detect Operator status). Without that
    /// claim, <see cref="ClaimsPrincipalMapper.Map"/> falls back to
    /// <see cref="PrincipalClassification.UntrustedExternal"/>, which is not Operator,
    /// so <c>ResolveReminderAuthorizationContext</c> returns null and the endpoint
    /// returns 403.
    /// </summary>
    private sealed class NonOperatorAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "NonOperatorTest";
        public const string HeaderName = "X-Test-NonOperator";
        public const string HeaderValue = "ok";

        public NonOperatorAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var value) || value != HeaderValue)
                return Task.FromResult(AuthenticateResult.NoResult());

            // Authenticated identity WITHOUT the netclaw:principal=Operator claim.
            // ClaimsPrincipalMapper.Map() will return UntrustedExternal for this principal.
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "device-user")],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
