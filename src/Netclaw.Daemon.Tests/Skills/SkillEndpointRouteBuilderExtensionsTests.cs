// -----------------------------------------------------------------------
// <copyright file="SkillEndpointRouteBuilderExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Skills;
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

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback, SkillRegistry registry, NetclawPaths paths)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(paths);

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
}
