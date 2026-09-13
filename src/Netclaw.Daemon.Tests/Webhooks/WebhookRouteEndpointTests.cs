// -----------------------------------------------------------------------
// <copyright file="WebhookRouteEndpointTests.cs" company="Petabridge, LLC">
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
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Webhooks;
using Netclaw.Configuration;
using Netclaw.Daemon.Reminders;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Webhooks;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

/// <summary>
/// The <c>/api/webhooks</c> management resource. The tests run against the real
/// <see cref="WebhookRouteActor"/> over a temporary route store, so the status
/// mapping and the persistence round trip are both exercised end to end.
/// </summary>
public sealed class WebhookRouteEndpointTests : IAsyncDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly WebhookRouteStore _store;
    private readonly ActorSystem _actorSystem;
    private readonly IActorRef _routeActor;

    public WebhookRouteEndpointTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _store = new WebhookRouteStore(_paths);

        _actorSystem = ActorSystem.Create($"webhook-route-endpoint-tests-{Guid.NewGuid():N}");
        _routeActor = _actorSystem.ActorOf(WebhookRouteActor.CreateProps(_store));
    }

    public async ValueTask DisposeAsync()
    {
        await _actorSystem.Terminate();
        _dir.Dispose();
    }

    private static object ValidRouteBody(string secret = "endpoint-secret") => new
    {
        prompt = "Handle inbound delivery.",
        secret,
        verificationKind = "Hmac"
    };

    // ── Auth ──

    /// <summary>
    /// Auth parity: the new resource is rejected by exactly the rules that
    /// already reject the sibling <c>/api/reminders</c> resource.
    /// </summary>
    [Fact]
    public async Task Unauthenticated_requests_are_rejected_like_the_sibling_api_surface()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var webhooks = await client.GetAsync("/api/webhooks", TestContext.Current.CancellationToken);
        var reminders = await client.GetAsync("/api/reminders", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, reminders.StatusCode);
        Assert.Equal(reminders.StatusCode, webhooks.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_PUT_writes_no_route_file()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);

        var response = await app.GetTestClient().PutAsJsonAsync(
            "/api/webhooks/unauthenticated-route", ValidRouteBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(_store.TryGet("unauthenticated-route", out _));
    }

    [Fact]
    public async Task NonOperator_PUT_is_forbidden_and_writes_no_route_file()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false, addNonOperatorScheme: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(NonOperatorAuthHandler.HeaderName, NonOperatorAuthHandler.HeaderValue);

        var response = await client.PutAsJsonAsync(
            "/api/webhooks/non-operator-route", ValidRouteBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(_store.TryGet("non-operator-route", out _));
    }

    // ── Status mapping ──

    [Fact]
    public async Task Upsert_persists_through_the_actor_and_returns_the_stored_route()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PutAsJsonAsync(
            "/api/webhooks/round-trip-route", ValidRouteBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("round-trip-route", json.GetProperty("name").GetString());
        Assert.Equal("Handle inbound delivery.", json.GetProperty("prompt").GetString());
        Assert.Equal("Hmac", json.GetProperty("verification").GetProperty("kind").GetString());
        // The management surface never echoes the route secret.
        Assert.DoesNotContain("endpoint-secret", body, StringComparison.Ordinal);

        // The actor wrote the route file, secret included.
        Assert.True(_store.TryGet("round-trip-route", out var stored));
        Assert.Equal(new SensitiveString("endpoint-secret"), stored.Definition!.Verification.Secret);
        Assert.Equal(TrustAudience.Personal, stored.Definition.Audience);
    }

    [Fact]
    public async Task Upsert_applies_a_field_level_patch_to_an_existing_route()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var created = await client.PutAsJsonAsync(
            "/api/webhooks/patched-route", ValidRouteBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var patched = await client.PutAsJsonAsync(
            "/api/webhooks/patched-route",
            new { rateLimitPerMinute = 5 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.True(_store.TryGet("patched-route", out var stored));
        Assert.Equal(5, stored.Definition!.RateLimitPerMinute);
        Assert.Equal("Handle inbound delivery.", stored.Definition.Prompt);
        Assert.Equal(new SensitiveString("endpoint-secret"), stored.Definition.Verification.Secret);
    }

    [Fact]
    public async Task Validation_failure_returns_400_with_the_validator_message_and_writes_no_file()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PutAsJsonAsync(
            "/api/webhooks/no-secret-route",
            new { prompt = "Handle inbound delivery.", verificationKind = "Hmac" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Verification secret is required.", json.GetProperty("error").GetString());
        Assert.False(_store.TryGet("no-secret-route", out _));
    }

    [Fact]
    public async Task An_unsupported_verification_kind_returns_400_before_the_actor_is_asked()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PutAsJsonAsync(
            "/api/webhooks/bad-kind-route",
            new { prompt = "Handle inbound delivery.", secret = "s", verificationKind = "quantum" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("verificationKind", json.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.False(_store.TryGet("bad-kind-route", out _));
    }

    [Fact]
    public async Task An_invalid_route_name_returns_400()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().PutAsJsonAsync(
            "/api/webhooks/Not_Kebab", ValidRouteBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("kebab-case", json.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_returns_the_route_without_its_secret()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        await client.PutAsJsonAsync("/api/webhooks/readable-route", ValidRouteBody(), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/webhooks/readable-route", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("endpoint-secret", body, StringComparison.Ordinal);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("readable-route", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Get_of_an_unknown_route_returns_404()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().GetAsync(
            "/api/webhooks/missing-route", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_returns_a_summary_for_every_route()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        await client.PutAsJsonAsync("/api/webhooks/alpha-route", ValidRouteBody(), TestContext.Current.CancellationToken);
        await client.PutAsJsonAsync("/api/webhooks/beta-route", ValidRouteBody(), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/webhooks", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("endpoint-secret", body, StringComparison.Ordinal);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(
            ["alpha-route", "beta-route"],
            json.EnumerateArray().Select(x => x.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Delete_returns_204_and_removes_the_route_file()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();
        await client.PutAsJsonAsync("/api/webhooks/doomed-route", ValidRouteBody(), TestContext.Current.CancellationToken);

        var response = await client.DeleteAsync("/api/webhooks/doomed-route", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(_store.TryGet("doomed-route", out _));
    }

    [Fact]
    public async Task Delete_of_an_unknown_route_returns_404()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);

        var response = await app.GetTestClient().DeleteAsync(
            "/api/webhooks/missing-route", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── App factory ──

    private async Task<WebApplication> CreateAppAsync(
        bool spoofLoopback,
        bool addNonOperatorScheme = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_paths);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddSingleton<IRequiredActor<WebhookRouteActorKey>>(
            new FakeRequiredActor<WebhookRouteActorKey>(_routeActor));

        // The reminders resource is mapped only as the auth-parity reference.
        // Its dependencies must resolve so minimal APIs bind them as services
        // rather than inferring them as request bodies.
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(new SchedulingConfig { Enabled = true });
        builder.Services.AddSingleton(new ReminderDefinitionStore(_paths));
        builder.Services.AddSingleton(new ReminderHistoryStore(_paths));
        builder.Services.AddSingleton<IRequiredActor<ReminderManagerActorKey>>(
            new FakeRequiredActor<ReminderManagerActorKey>(_actorSystem.DeadLetters));

        if (addNonOperatorScheme)
        {
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
        app.MapWebhookRouteEndpoints();
        // Mapped only as the auth-parity reference surface; its handlers are
        // never reached because the parity assertion is unauthenticated.
        app.MapReminderEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    // ── Fakes and helpers ──

    private sealed class FakeRequiredActor<TKey>(IActorRef actorRef) : IRequiredActor<TKey>
    {
        public IActorRef ActorRef => actorRef;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(actorRef);
    }

    /// <summary>
    /// Authenticates a request carrying <c>X-Test-NonOperator</c> as an
    /// authenticated principal WITHOUT the Operator claim, so
    /// <see cref="ClaimsPrincipalMapper"/> classifies it as
    /// <see cref="PrincipalClassification.UntrustedExternal"/>.
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

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "device-user")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
