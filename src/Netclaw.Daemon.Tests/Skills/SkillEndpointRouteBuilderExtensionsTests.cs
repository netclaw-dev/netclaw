// -----------------------------------------------------------------------
// <copyright file="SkillEndpointRouteBuilderExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Skills;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Skills;

/// <summary>
/// Integration tests for <c>GET /api/skills</c>
/// (<see cref="SkillEndpointRouteBuilderExtensions.MapSkillEndpoints"/>). The test
/// host calls the real extension method — no handler reimplementation — and the
/// registry is seeded with both a file skill and a dynamic MCP prompt skill.
/// </summary>
public sealed class SkillEndpointRouteBuilderExtensionsTests : IDisposable
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<WebApplication> CreateAppAsync(
        bool spoofLoopback,
        SkillRegistry registry,
        NetclawPaths paths,
        ServerFeedSkillSyncService? syncService = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(syncService ?? CreateSyncService(registry, paths));

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
        app.MapSkillEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task RequiresAuthorization_returns_401_for_unauthenticated_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = new NetclawPaths(_dir.Path);
        await using var app = await CreateAppAsync(spoofLoopback: false, new SkillRegistry(), paths);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/skills", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sync_requires_authorization_for_an_unauthenticated_post()
    {
        var paths = new NetclawPaths(_dir.Path);
        await using var app = await CreateAppAsync(spoofLoopback: false, new SkillRegistry(), paths);

        var response = await app.GetTestClient().PostAsync("/api/skills/sync", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sync_returns_503_when_the_service_is_not_started()
    {
        var paths = new NetclawPaths(_dir.Path);
        await using var app = await CreateAppAsync(spoofLoopback: true, new SkillRegistry(), paths);

        var response = await app.GetTestClient().PostAsync("/api/skills/sync", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Sync_post_returns_503_when_host_stop_cancels_the_joined_pass()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var registry = new SkillRegistry();
        var handler = new BlockingFeedHandler();
        var logger = new JoinSignalLogger();
        using var syncService = CreateBlockingSyncService(registry, paths, handler, logger);
        await syncService.StartAsync(TestContext.Current.CancellationToken);
        await handler.IndexRequest.Task;

        await using var app = await CreateAppAsync(spoofLoopback: true, registry, paths, syncService);
        var post = app.GetTestClient().PostAsync("/api/skills/sync", null, TestContext.Current.CancellationToken);
        await logger.Joined.Task.WaitAsync(TestContext.Current.CancellationToken);
        await syncService.StopAsync(TestContext.Current.CancellationToken);

        var response = await post;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Skill sync stopped", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Concurrent_sync_posts_share_a_pass_and_serialize_its_complete_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var registry = new SkillRegistry();
        var handler = new BlockingFeedHandler();
        var logger = new JoinSignalLogger();
        using var syncService = CreateBlockingSyncService(registry, paths, handler, logger);
        await syncService.StartAsync(ct);
        try
        {
            await handler.IndexRequest.Task.WaitAsync(ct);
            await using var app = await CreateAppAsync(spoofLoopback: true, registry, paths, syncService);
            using var client = app.GetTestClient();
            var firstPost = client.PostAsync("/api/skills/sync", null, ct);
            var secondPost = client.PostAsync("/api/skills/sync", null, ct);
            await logger.BothJoined.Task.WaitAsync(ct);

            handler.CompleteIndex();
            using var firstResponse = await firstPost;
            using var secondResponse = await secondPost;
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            var firstJson = await firstResponse.Content.ReadAsStringAsync(ct);
            var secondJson = await secondResponse.Content.ReadAsStringAsync(ct);
            Assert.Equal(firstJson, secondJson);

            using var wire = JsonDocument.Parse(firstJson);
            Assert.False(wire.RootElement.TryGetProperty("succeeded", out _));
            var result = JsonSerializer.Deserialize<SkillSyncResult.Response>(firstJson, ReadOptions);
            Assert.NotNull(result);
            Assert.False(string.IsNullOrWhiteSpace(result.PassId));
            var source = Assert.Single(result.Sources);
            Assert.Equal("team", source.Name);
            Assert.Equal(1, source.ChangedCount);
            Assert.Equal(0, source.UnchangedCount);
            Assert.Equal(0, source.RejectedCount);
            Assert.Equal(0, source.FailedCount);
            Assert.Equal("absent", source.Sidecar);
            Assert.Null(source.Error);
            Assert.True(result.Inventory.Succeeded);
            Assert.Equal(1, result.Inventory.AcceptedCount);
            Assert.Equal(0, result.Inventory.RejectedCount);
            Assert.Null(result.Inventory.Error);
            Assert.NotNull(registry.GetByName("route-proof"));
            Assert.Equal(1, handler.IndexRequestCount);
        }
        finally
        {
            await syncService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Returns_dynamic_mcp_prompt_skills_that_a_disk_scan_cannot_see()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        var registry = new SkillRegistry();

        // A file skill under the native skills directory.
        var fileSkill = new SkillEntry(
            "demo-file",
            "Demo File",
            "A file-backed skill.",
            new FileSkillSource(
                Path.Combine(paths.SkillsDirectory, "demo-file", "SKILL.md"),
                Path.Combine(paths.SkillsDirectory, "demo-file")),
            Category: null);
        registry.ReplaceAll([fileSkill]);

        // A dynamic MCP prompt skill — exists only in memory, never on disk.
        var mcpSkill = new SkillEntry(
            "mcp__demo__hello",
            "hello",
            "A demo MCP prompt.",
            new McpPromptSkillSource(
                "demo",
                "hello",
                Generation: 1,
                Arguments: [new SkillArgumentDescriptor("property", "The property slug.", Required: true)]),
            Category: "mcp")
        {
            UserInvocable = false,
            ArgumentHint = "<property>",
        };
        registry.PublishMcpPromptSkills("demo", [mcpSkill]);

        await using var app = await CreateAppAsync(spoofLoopback: true, registry, paths);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/skills", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(ct);
        var inventory = JsonSerializer.Deserialize<SkillInventory.Response>(json, ReadOptions);
        Assert.NotNull(inventory);

        // The MCP prompt skill is present, tagged as its dynamic source, with the
        // metadata a client needs to present it.
        var mcp = Assert.Single(inventory!.Skills, s => s.Name == "mcp__demo__hello");
        Assert.Equal("mcp", mcp.Source);
        Assert.Equal("demo", mcp.ServerName);
        Assert.Equal("hello", mcp.PromptName);
        Assert.Equal("A demo MCP prompt.", mcp.Description);
        Assert.Equal("<property>", mcp.ArgumentHint);
        Assert.False(mcp.UserInvocable);   // hidden from /name invocation
        Assert.True(mcp.ModelInvocable);   // still in the model's compressed index

        var arg = Assert.Single(mcp.Arguments!);
        Assert.Equal("property", arg.Name);
        Assert.True(arg.Required);

        // The file skill is present too, classified by its path.
        var file = Assert.Single(inventory.Skills, s => s.Name == "demo-file");
        Assert.Equal("native", file.Source);
        Assert.Null(file.ServerName);
    }

    private static ServerFeedSkillSyncService CreateSyncService(SkillRegistry registry, NetclawPaths paths)
    {
        var publisher = new SkillIndexPublisher(
            registry,
            new SkillIndexContextLayer(),
            static (_, _) => true);
        return new ServerFeedSkillSyncService(
            new SkillFeedsConfig(),
            paths,
            new SkillInventoryRefresher(paths, new SkillFeedsConfig(), [], registry, publisher),
            TimeProvider.System,
            new NoOpSkillContentScanner(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerFeedSkillSyncService>.Instance);
    }

    private static ServerFeedSkillSyncService CreateBlockingSyncService(
        SkillRegistry registry,
        NetclawPaths paths,
        BlockingFeedHandler handler,
        ILogger<ServerFeedSkillSyncService> logger)
    {
        var feeds = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds = [new SkillFeedSource { Name = "team", Url = "https://feed.test/", TimeoutSeconds = 30 }],
        };
        var publisher = new SkillIndexPublisher(registry, new SkillIndexContextLayer(), static (_, _) => true);
        return new ServerFeedSkillSyncService(
            feeds,
            paths,
            registry,
            publisher,
            TimeProvider.System,
            new NoOpSkillContentScanner(),
            logger,
            [],
            feed => new Netclaw.SkillClient.SkillServerClient(new HttpClient(handler)
            {
                BaseAddress = new Uri(feed.Url),
            }),
            TimeSpan.Zero);
    }

    private sealed class BlockingFeedHandler : HttpMessageHandler
    {
        private const string SkillContent = "---\nname: route-proof\ndescription: Route proof.\n---\n\nRoute proof body.\n";
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _indexRequestCount;

        public TaskCompletionSource IndexRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int IndexRequestCount => Volatile.Read(ref _indexRequestCount);

        public void CompleteIndex() => _response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                skills = new[]
                {
                    new
                    {
                        name = "route-proof",
                        description = "Route proof.",
                        url = "https://feed.test/route-proof/SKILL.md",
                        version = "1.0.0",
                        digest = "sha256:" + SkillSyncHelpers.ComputeSha256(SkillContent),
                    },
                },
            }),
        });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/route-proof/SKILL.md")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SkillContent) };
            if (request.RequestUri.AbsolutePath != "/.well-known/agent-skills/index.json")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            Interlocked.Increment(ref _indexRequestCount);
            IndexRequest.TrySetResult();
            return await _response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class JoinSignalLogger : ILogger<ServerFeedSkillSyncService>
    {
        private int _joinedCount;
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) == "Joined the active external skill sync pass.")
            {
                Joined.TrySetResult();
                if (Interlocked.Increment(ref _joinedCount) == 2)
                    BothJoined.TrySetResult();
            }
        }
    }
}
