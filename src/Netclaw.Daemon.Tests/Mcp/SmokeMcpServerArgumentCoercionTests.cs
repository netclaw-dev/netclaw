// -----------------------------------------------------------------------
// <copyright file="SmokeMcpServerArgumentCoercionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Providers.OAuth;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// End-to-end coverage for schema-driven argument coercion. Drives a
/// deliberately mis-encoded tool call through the real netclaw → MCP wire path
/// — <see cref="McpToolAdapter"/> coercion → <see cref="McpClientManager"/> →
/// stdio JSON-RPC → the deterministic <c>Netclaw.SmokeMcpServer</c> — and
/// asserts the server received the structured shape its schema declares.
/// The <c>CoerceArguments</c> unit tests cover the coercion logic in isolation;
/// this test proves the wire: that a reconstructed argument actually serializes
/// over JSON-RPC and is accepted by a real MCP server.
/// </summary>
public sealed class SmokeMcpServerArgumentCoercionTests
{
    [Fact]
    public async Task StringifiedArrayArgument_IsReconstructed_OverTheWire()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = "dotnet",
            Arguments = [LocateSmokeMcpServer()],
            Enabled = true,
        };

        var registry = new ToolRegistry();
        var pkceService = new OAuthPkceService(new HttpClient());
        var oauthService = new McpOAuthService(
            new HttpClient(),
            new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())),
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            pkceService,
            NullNotificationSink.Instance);
        var manager = new McpClientManager(
            new Dictionary<string, McpServerEntry> { ["smoke"] = entry },
            registry,
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            NullLogger<McpClientManager>.Instance);

        try
        {
            await manager.StartAsync(ct);

            var recordTasks = registry.GetAllRegistrations()
                .Select(r => r.Tool)
                .OfType<McpToolAdapter>()
                .SingleOrDefault(t => t.Name == "smoke/record-tasks");
            Assert.NotNull(recordTasks);

            // `tasks` arrives the way a provider SDK delivers a model that
            // double-encoded the argument: a JsonElement of ValueKind.String
            // whose text is a JSON array. `reference` is a zero-padded string.
            var args = new Dictionary<string, object?>
            {
                ["tasks"] = JsonSerializer.SerializeToElement(
                    "[{\"content\":\"A\"},{\"content\":\"B\"}]"),
                ["reference"] = "00713",
            };

            var result = await recordTasks!.ExecuteAsync(args, ToolExecutionContext.Empty, ct);

            // count=2 proves the stringified array was reconstructed before the
            // server bound it to `object[]` — a raw string would fail to bind.
            Assert.Contains("count=2", result);
            // reference=00713 proves the string-typed argument was not coerced
            // to the integer 713 (the schema-blind corruption class).
            Assert.Contains("reference=00713", result);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            manager.Dispose();
        }
    }

    /// <summary>
    /// Resolves the built <c>Netclaw.SmokeMcpServer.dll</c>. The server is a
    /// <c>ReferenceOutputAssembly=false</c> project reference, so it is always
    /// built and lands in its own bin under the same configuration and TFM as
    /// this test assembly.
    /// </summary>
    private static string LocateSmokeMcpServer()
    {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = testBin.Name;
        var config = testBin.Parent!.Name;

        var repo = testBin;
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Netclaw.slnx")))
            repo = repo.Parent;
        Assert.NotNull(repo);

        var dll = Path.Combine(
            repo!.FullName, "tests", "Netclaw.SmokeMcpServer", "bin", config, tfm,
            "Netclaw.SmokeMcpServer.dll");
        Assert.True(File.Exists(dll), $"Smoke MCP server not built at: {dll}");
        return dll;
    }
}
