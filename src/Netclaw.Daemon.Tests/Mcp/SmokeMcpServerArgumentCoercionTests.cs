// -----------------------------------------------------------------------
// <copyright file="SmokeMcpServerArgumentCoercionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// End-to-end coverage for schema-driven argument coercion. Drives a
/// deliberately mis-encoded tool call through the real netclaw → MCP wire path
/// — <see cref="McpToolAdapter"/> coercion → <see cref="Netclaw.Daemon.Mcp.McpClientManager"/>
/// → stdio JSON-RPC → the deterministic <c>Netclaw.SmokeMcpServer</c> — and
/// asserts the server received the structured shape its schema declares.
/// The <c>CoerceArguments</c> unit tests cover the coercion logic in isolation;
/// this test proves the wire: that a reconstructed argument actually serializes
/// over JSON-RPC and is accepted by a real MCP server.
/// </summary>
public sealed class SmokeMcpServerArgumentCoercionTests(ITestOutputHelper output)
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
            Arguments = [SmokeMcpServerLocator.LocateDll()],
            Enabled = true,
        };

        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke"] = entry }, registry, output);

        await harness.Manager.StartAsync(ct);
        harness.AssertConnected("smoke");

        var recordTasks = registry.GetAllRegistrations()
            .Select(r => r.Tool)
            .OfType<McpToolAdapter>()
            .SingleOrDefault(t => t.Name == "smoke/record-tasks");
        Assert.NotNull(recordTasks);

        // `tasks` arrives the way a provider SDK delivers a model that
        // double-encoded the argument: a JsonElement of ValueKind.String whose
        // text is a JSON array. `reference` is a zero-padded string.
        var args = new Dictionary<string, object?>
        {
            ["tasks"] = JsonSerializer.SerializeToElement(
                "[{\"content\":\"A\"},{\"content\":\"B\"}]"),
            ["reference"] = "00713",
        };

        var result = await recordTasks!.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), ct);

        // count=2 (with the trailing delimiter) proves the stringified array was
        // reconstructed before the server bound it to `object[]` — a raw string
        // would fail to bind and the call would error.
        Assert.Contains("count=2 ", result);
        // reference=00713 proves the string-typed argument was not coerced to
        // the integer 713 (the schema-blind corruption class).
        Assert.Contains("reference=00713", result);
    }

}
