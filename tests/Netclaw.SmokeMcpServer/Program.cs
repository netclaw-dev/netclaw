// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Deterministic stdio MCP test server for the native smoke harness.
//
// Exposes exactly two tools whose output is a pure function of their input:
//   add(a, b) -> a + b
//   echo(text) -> text
//
// Determinism is the whole point: add(2, 2) is always 4, so a smoke
// scenario can hard-assert on the tool RESULT even though the surrounding
// LLM prose is random.
//
// CRITICAL: stdout is the MCP JSON-RPC protocol channel. Nothing other than
// protocol frames may be written to stdout. All diagnostics go to stderr.

namespace Netclaw.SmokeMcpServer;

internal static class Program
{
    /// <summary>Deterministic tool: returns the integer sum of two integers.</summary>
    [Description("Add two integers and return their sum.")]
    private static int Add(
        [Description("The first integer addend.")] int a,
        [Description("The second integer addend.")] int b) => a + b;

    /// <summary>Deterministic tool: echoes its input back verbatim.</summary>
    [Description("Echo the given text back verbatim.")]
    private static string Echo(
        [Description("The text to echo back unchanged.")] string text) => text;

    private static async Task<int> Main()
    {
        try
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>
            {
                McpServerTool.Create(Add, new McpServerToolCreateOptions { Name = "add" }),
                McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" }),
            };

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = "netclaw-smoke-mcp",
                    Version = "1.0.0",
                },
                ServerInstructions =
                    "Deterministic test server for the Netclaw smoke harness. " +
                    "Use 'add' to sum two integers and 'echo' to repeat text.",
                ToolCollection = tools,
            };

            await using var transport = new StdioServerTransport(options);
            await using var server = McpServer.Create(transport, options);
            await server.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            // stderr only — stdout is reserved for the JSON-RPC protocol.
            await Console.Error.WriteLineAsync($"[smoke-mcp:error] {ex}");
            return 1;
        }
    }
}
