// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncToolIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ServerFeedSkillSyncToolIntegrationTests : IDisposable
{
    private const string FeedUrl = "https://tool-proof.test/";
    private readonly DisposableTempDir _directory = new();
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _registry = new();

    public ServerFeedSkillSyncToolIntegrationTests()
    {
        _paths = new NetclawPaths(_directory.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task SyncAsync_updates_the_live_registry_for_SkillReadResourceTool()
    {
        var handler = new RevisionFeedHandler();
        using var service = CreateService(handler);
        var tool = new SkillReadResourceTool(_registry, new NoOpSkillContentScanner());
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
        });
        await service.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await service.SyncAsync(TestContext.Current.CancellationToken);
            var initialResource = await tool.ExecuteAsync(
                ToolInput.Create(
                    "SkillName", "feed-resource-proof",
                    "ResourcePath", "references/proof.txt"),
                context,
                TestContext.Current.CancellationToken);
            Assert.Equal("logical resource bytes: A\n", initialResource);

            handler.UseRevision("B");

            var result = await service.SyncAsync(TestContext.Current.CancellationToken);
            var source = Assert.Single(result.Sources);
            Assert.Equal(1, source.ChangedCount);

            var resource = await tool.ExecuteAsync(
                ToolInput.Create(
                    "SkillName", "feed-resource-proof",
                    "ResourcePath", "references/proof.txt"),
                context,
                TestContext.Current.CancellationToken);

            Assert.Equal("logical resource bytes: B\n", resource);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private ServerFeedSkillSyncService CreateService(RevisionFeedHandler handler)
    {
        var feeds = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds = [new SkillFeedSource { Name = "tool-proof", Url = FeedUrl, TimeoutSeconds = 30 }],
        };
        var publisher = new SkillIndexPublisher(
            _registry,
            new SkillIndexContextLayer(),
            static (_, _) => true);
        return new ServerFeedSkillSyncService(
            feeds,
            _paths,
            _registry,
            publisher,
            TimeProvider.System,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            [],
            feed => new SkillServerClient(new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri(feed.Url),
            }),
            TimeSpan.Zero);
    }

    private sealed class RevisionFeedHandler : HttpMessageHandler
    {
        private string _revision = "A";

        public void UseRevision(string revision) => _revision = revision;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = _revision;
            var main = $$"""
                ---
                name: feed-resource-proof
                description: Resource proof {{revision}}.
                ---

                # Feed resource proof
                """;
            var resource = $"logical resource bytes: {revision}\n";
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/.well-known/agent-skills/index.json")
            {
                var index = new
                {
                    skills = new[]
                    {
                        new
                        {
                            name = "feed-resource-proof",
                            type = "skill",
                            description = "Feed resource proof.",
                            url = FeedUrl + "skill.md",
                            digest = "sha256:" + Digest(main),
                            version = revision,
                            resources = new[]
                            {
                                new
                                {
                                    path = "references/proof.txt",
                                    url = FeedUrl + "proof.txt",
                                    digest = "sha256:" + Digest(resource),
                                },
                            },
                        },
                    },
                };
                return Task.FromResult(JsonResponse(index));
            }

            if (path == "/skill.md")
                return Task.FromResult(TextResponse(main));
            if (path == "/proof.txt")
                return Task.FromResult(TextResponse(resource));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string Digest(string content)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        private static HttpResponseMessage JsonResponse<T>(T content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage TextResponse(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain"),
        };
    }
}
