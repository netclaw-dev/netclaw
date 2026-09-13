// -----------------------------------------------------------------------
// <copyright file="WebhookRouteActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Webhooks;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Webhooks.WebhookRouteProtocol;

namespace Netclaw.Actors.Tests.Webhooks;

/// <summary>
/// The actor is the single mutation authority for webhook route files. These
/// tests assert on message outcomes and on the resulting file, never on thread
/// scheduling or elapsed time.
/// </summary>
public class WebhookRouteActorTests : TestKit
{
    private readonly DisposableTempDir _dir = new();
    private NetclawPaths _paths = null!;
    private WebhookRouteStore _store = null!;

    public WebhookRouteActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _store = new WebhookRouteStore(_paths);

        builder.StartActors((system, registry, _) =>
        {
            var actor = system.ActorOf(WebhookRouteActor.CreateProps(_store), "webhook-routes");
            registry.Register<WebhookRouteActorKey>(actor);
        });
    }

    protected override async Task AfterAllAsync()
    {
        _dir.Dispose();
        await base.AfterAllAsync();
    }

    private IActorRef RouteActor => ActorRegistry.For(Sys).Get<WebhookRouteActorKey>();

    private static UpsertRoute NewRoute(string routeName) => new()
    {
        RouteName = WebhookRouteName.Create(routeName),
        CreatorAudience = TrustAudience.Personal,
        Prompt = "Handle inbound delivery.",
        Secret = "original-secret",
        VerificationKind = WebhookVerifierKind.Hmac
    };

    private string RouteFilePath(string routeName)
        => Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json");

    private async Task CreateRouteAsync(string routeName)
    {
        var created = await RouteActor.Ask<RouteSaved>(
            NewRoute(routeName), TestContext.Current.CancellationToken);
        Assert.Equal(RouteSaveOutcome.Created, created.Outcome);
    }

    /// <summary>
    /// The lost-update proof. Two field-level patches of the same route arrive
    /// back to back. Each is a read-modify-write inside one message turn, so the
    /// second patch reads the first patch's result and neither field is lost.
    /// The mailbox does the serializing — no thread choreography is involved.
    /// </summary>
    [Fact]
    public async Task Concurrent_field_level_updates_lose_neither_field()
    {
        await CreateRouteAsync("concurrent-route");

        // Both patches are in the mailbox before either reply is read.
        RouteActor.Tell(
            new UpsertRoute
            {
                RouteName = WebhookRouteName.Create("concurrent-route"),
                CreatorAudience = TrustAudience.Personal,
                Prompt = "Patched by the first writer."
            },
            TestActor);
        RouteActor.Tell(
            new UpsertRoute
            {
                RouteName = WebhookRouteName.Create("concurrent-route"),
                CreatorAudience = TrustAudience.Personal,
                RateLimitPerMinute = 99
            },
            TestActor);

        var first = await ExpectMsgAsync<RouteSaved>(cancellationToken: TestContext.Current.CancellationToken);
        var second = await ExpectMsgAsync<RouteSaved>(cancellationToken: TestContext.Current.CancellationToken);
        // Both patches found the route on disk, so both report Updated.
        Assert.Equal(RouteSaveOutcome.Updated, first.Outcome);
        Assert.Equal(RouteSaveOutcome.Updated, second.Outcome);

        var response = await RouteActor.Ask<RouteResponse>(
            new GetRoute(WebhookRouteName.Create("concurrent-route")), TestContext.Current.CancellationToken);

        Assert.True(response.Found);
        var route = Assert.IsType<WebhookRouteConfig>(response.Route);
        Assert.Equal("Patched by the first writer.", route.Prompt);
        Assert.Equal(99, route.RateLimitPerMinute);
        // Neither patch carried a secret, so both preserved the stored one.
        Assert.Equal(new SensitiveString("original-secret"), route.Verification.Secret);
    }

    [Fact]
    public async Task Validation_rejection_writes_no_file_for_a_new_route()
    {
        var response = await RouteActor.Ask<RouteSaved>(
            new UpsertRoute
            {
                RouteName = WebhookRouteName.Create("invalid-new-route"),
                CreatorAudience = TrustAudience.Personal,
                Prompt = "Handle inbound delivery."
                // No secret: WebhookRouteValidator rejects the merged definition.
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteSaveOutcome.ValidationRejected, response.Outcome);
        Assert.Equal("Verification secret is required.", response.ErrorMessage);
        Assert.False(File.Exists(RouteFilePath("invalid-new-route")));
    }

    [Fact]
    public async Task Validation_rejection_leaves_an_existing_route_file_unchanged()
    {
        await CreateRouteAsync("guarded-route");
        var before = await File.ReadAllTextAsync(
            RouteFilePath("guarded-route"), TestContext.Current.CancellationToken);

        var response = await RouteActor.Ask<RouteSaved>(
            new UpsertRoute
            {
                RouteName = WebhookRouteName.Create("guarded-route"),
                CreatorAudience = TrustAudience.Personal,
                MaxBodyBytes = 0
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteSaveOutcome.ValidationRejected, response.Outcome);
        Assert.Equal("MaxBodyBytes must be >= 1.", response.ErrorMessage);

        var after = await File.ReadAllTextAsync(
            RouteFilePath("guarded-route"), TestContext.Current.CancellationToken);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// Required-ness lives on the merged definition, not on the patch. The
    /// patch may leave the prompt out, but the merged route may not: a webhook
    /// without a prompt has nothing to run.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task A_patch_that_blanks_the_prompt_is_rejected(string blankPrompt)
    {
        await CreateRouteAsync("prompted-route");

        var response = await RouteActor.Ask<RouteSaved>(
            new UpsertRoute
            {
                RouteName = WebhookRouteName.Create("prompted-route"),
                CreatorAudience = TrustAudience.Personal,
                Prompt = blankPrompt
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteSaveOutcome.ValidationRejected, response.Outcome);
        Assert.Equal("Prompt is required.", response.ErrorMessage);
        Assert.True(_store.TryGet("prompted-route", out var stored));
        Assert.Equal("Handle inbound delivery.", stored.Definition!.Prompt);
    }

    [Fact]
    public async Task A_route_above_the_creator_authority_is_not_overwritten()
    {
        await CreateRouteAsync("personal-route");

        var response = await RouteActor.Ask<RouteSaved>(
            new UpsertRoute
            {
                RouteName = WebhookRouteName.Create("personal-route"),
                CreatorAudience = TrustAudience.Public,
                Prompt = "Take over the route."
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteSaveOutcome.AuthorityRejected, response.Outcome);
        Assert.True(_store.TryGet("personal-route", out var stored));
        Assert.Equal("Handle inbound delivery.", stored.Definition!.Prompt);
    }

    /// <summary>
    /// The security audit trail. A refused mutation is the one signal that a
    /// caller tried to take over authority above its own, so the actor records
    /// it at warning level with both audiences.
    /// </summary>
    [Fact]
    public async Task An_authority_rejection_is_recorded_in_the_log()
    {
        await CreateRouteAsync("audited-route");

        await EventFilter
            .Warning(contains: "audited-route")
            .ExpectOneAsync(async () =>
            {
                var response = await RouteActor.Ask<RouteSaved>(
                    new UpsertRoute
                    {
                        RouteName = WebhookRouteName.Create("audited-route"),
                        CreatorAudience = TrustAudience.Public,
                        Prompt = "Take over the route."
                    },
                    TestContext.Current.CancellationToken);
                Assert.Equal(RouteSaveOutcome.AuthorityRejected, response.Outcome);
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A new incarnation of the actor serves the route the previous incarnation
    /// wrote. The actor keeps no cache, so a restart has nothing to lose and
    /// rebuilds its whole answer from the route files.
    /// </summary>
    [Fact]
    public async Task A_new_incarnation_rebuilds_its_answers_from_disk()
    {
        await CreateRouteAsync("survivor-route");

        await WatchAsync(RouteActor);
        RouteActor.Tell(PoisonPill.Instance);
        await ExpectTerminatedAsync(RouteActor, cancellationToken: TestContext.Current.CancellationToken);

        var replacement = Sys.ActorOf(WebhookRouteActor.CreateProps(_store));
        var response = await replacement.Ask<RouteResponse>(
            new GetRoute(WebhookRouteName.Create("survivor-route")), TestContext.Current.CancellationToken);

        Assert.True(response.Found);
        Assert.Equal("Handle inbound delivery.", response.Route!.Prompt);
    }

    /// <summary>
    /// Version-skew tolerance. An old CLI writes a route file directly, behind
    /// the actor. The next read through the actor returns the file's content
    /// because every read goes to disk.
    /// </summary>
    [Fact]
    public async Task An_external_writer_change_is_visible_to_the_next_actor_read()
    {
        await CreateRouteAsync("skew-route");

        // A second store instance stands in for the old CLI process.
        var externalWriter = new WebhookRouteStore(_paths);
        externalWriter.Save("skew-route", new WebhookRouteConfig
        {
            Prompt = "Written by an old CLI.",
            RateLimitPerMinute = 7,
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("external-secret")
            }
        });

        var response = await RouteActor.Ask<RouteResponse>(
            new GetRoute(WebhookRouteName.Create("skew-route")), TestContext.Current.CancellationToken);

        Assert.True(response.Found);
        Assert.Equal("Written by an old CLI.", response.Route!.Prompt);
        Assert.Equal(7, response.Route.RateLimitPerMinute);

        // A later patch merges onto the external content instead of the actor's
        // pre-skew view of the route.
        var patched = await RouteActor.Ask<RouteSaved>(
            new UpsertRoute
            {
                RouteName = WebhookRouteName.Create("skew-route"),
                CreatorAudience = TrustAudience.Personal,
                MaxBodyBytes = 2048
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteSaveOutcome.Updated, patched.Outcome);
        Assert.Equal("Written by an old CLI.", patched.Route!.Prompt);
        Assert.Equal(2048, patched.Route.MaxBodyBytes);
    }

    [Fact]
    public async Task Delete_reports_whether_the_route_existed()
    {
        await CreateRouteAsync("doomed-route");

        var deleted = await RouteActor.Ask<RouteDeleted>(
            new DeleteRoute(WebhookRouteName.Create("doomed-route")), TestContext.Current.CancellationToken);
        Assert.True(deleted.Found);
        Assert.False(File.Exists(RouteFilePath("doomed-route")));

        var again = await RouteActor.Ask<RouteDeleted>(
            new DeleteRoute(WebhookRouteName.Create("doomed-route")), TestContext.Current.CancellationToken);
        Assert.False(again.Found);
    }

    [Fact]
    public async Task List_returns_every_route_file()
    {
        await CreateRouteAsync("alpha-route");
        await CreateRouteAsync("beta-route");

        var response = await RouteActor.Ask<RouteListResponse>(
            ListRoutes.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(["alpha-route", "beta-route"], response.Routes.Select(x => x.RouteName));
        Assert.All(response.Routes, entry => Assert.NotNull(entry.Definition));
    }
}
