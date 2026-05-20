// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Deterministic stdio MCP test server for the native smoke harness.
//
// Exposes exactly three tools whose output is a pure function of their input:
//   add(a, b)                -> a + b
//   echo(text)               -> text
//   record-tasks(tasks, ref) -> a summary of the structured arguments received
//
// Determinism is the whole point: add(2, 2) is always 4, so a smoke
// scenario can hard-assert on the tool RESULT even though the surrounding
// LLM prose is random. `record-tasks` additionally gives netclaw's
// schema-driven argument coercion a tool with an array-of-objects parameter
// to exercise end to end (see SmokeMcpServerArgumentCoercionTests).
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

    /// <summary>
    /// Deterministic tool with a structured (array-of-objects) parameter. Its
    /// declared schema types <c>tasks</c> as <c>array</c> and <c>reference</c>
    /// as <c>string</c>, so it exercises netclaw's schema-driven argument
    /// coercion end to end: a model that double-encodes <c>tasks</c> as a JSON
    /// string must have it reconstructed into a real array before this server
    /// sees it, and <c>reference</c> must survive as a string (not coerced to a
    /// number — a value like <c>"00713"</c> would otherwise lose its leading
    /// zeros). The result echoes the received shape so a test can hard-assert
    /// on it.
    /// </summary>
    [Description("Record a batch of task objects under a reference code; echoes back the structured arguments received.")]
    private static string RecordTasks(
        [Description("The batch of task objects to record.")] object[] tasks,
        [Description("A reference code for the batch.")] string reference)
    {
        var kinds = string.Join(",", tasks.Select(t => t is JsonElement je ? je.ValueKind.ToString() : "?"));
        return $"reference={reference} count={tasks.Length} kinds=[{kinds}]";
    }

    private static async Task<int> Main()
    {
        try
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>
            {
                McpServerTool.Create(Add, new McpServerToolCreateOptions { Name = "add" }),
                McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = "echo" }),
                McpServerTool.Create(RecordTasks, new McpServerToolCreateOptions { Name = "record-tasks" }),
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
                    "Use 'add' to sum two integers, 'echo' to repeat text, and " +
                    "'record-tasks' to record a batch of task objects.",
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
