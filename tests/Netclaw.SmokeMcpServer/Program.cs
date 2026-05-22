// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Deterministic MCP test server for the native smoke harness and xUnit tests.
//
// Two modes:
//   stdio (default)
//     Exposes three fully-deterministic tools whose output is a pure
//     function of their input:
//       add(a, b)                -> a + b
//       echo(text)               -> text
//       record-tasks(tasks, ref) -> a summary of the structured arguments
//
//     Determinism is the whole point: add(2, 2) is always 4, so a smoke
//     scenario can hard-assert on the tool RESULT even though the
//     surrounding LLM prose is random. `record-tasks` additionally gives
//     netclaw's schema-driven argument coercion a tool with an
//     array-of-objects parameter to exercise end to end (see
//     SmokeMcpServerArgumentCoercionTests).
//
//     CRITICAL: stdout is the MCP JSON-RPC protocol channel. Nothing
//     other than protocol frames may be written to stdout. All
//     diagnostics go to stderr.
//
//   http  (opt-in via `--transport http --port N`)
//     Adds a `last_auth_header` tool that returns the Authorization
//     header attached to the *most recent* request the server saw.
//     `--port 0` asks the kernel for an ephemeral port; the chosen
//     port is printed to stderr as `[smoke-mcp:listening] http://127.0.0.1:NNNNN/mcp`
//     so a test can parse it without racing on a fixed port.
//
//     The `last_auth_header` tool is the only reliable end-to-end probe
//     that `netclaw mcp add --header "Authorization: …"` actually
//     attaches the configured header to outbound HTTP MCP traffic.
//     Plain string headers used to be silently dropped because
//     McpServerEntry stored them as Dictionary<string, string> and
//     SensitiveStringTypeConverter never decrypted the ENC:… ciphertext.

namespace Netclaw.SmokeMcpServer;

internal sealed class Program
{
    // Class is non-static so WithTools<Program>() can construct it for tool
    // discovery; tool methods themselves remain static because they have no
    // per-instance state. Static-only state lives on HttpAuthCapture.
    /// <summary>Deterministic tool: returns the integer sum of two integers.</summary>
    [McpServerTool(Name = "add")]
    [Description("Add two integers and return their sum.")]
    public static int Add(
        [Description("The first integer addend.")] int a,
        [Description("The second integer addend.")] int b) => a + b;

    /// <summary>Deterministic tool: echoes its input back verbatim.</summary>
    [McpServerTool(Name = "echo")]
    [Description("Echo the given text back verbatim.")]
    public static string Echo(
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
    [McpServerTool(Name = "record-tasks")]
    [Description("Record a batch of task objects under a reference code; echoes back the structured arguments received.")]
    public static string RecordTasks(
        [Description("The batch of task objects to record.")] object[] tasks,
        [Description("A reference code for the batch.")] string reference)
    {
        var kinds = string.Join(",", tasks.Select(t => t is JsonElement je ? je.ValueKind.ToString() : "?"));
        return $"reference={reference} count={tasks.Length} kinds=[{kinds}]";
    }

    /// <summary>
    /// HTTP-mode-only tool: returns the Authorization header attached to
    /// the most recent request the server received. Returns the literal
    /// string "(none)" when no request has carried an Authorization
    /// header yet. The "(none)" sentinel is deliberate — empty-string vs.
    /// null vs. missing-header is exactly the distinction we want a
    /// header-passthrough regression to flag.
    /// </summary>
    [McpServerTool(Name = "last_auth_header")]
    [Description("Returns the Authorization header attached to the most recent incoming request, or '(none)' if no such header has been seen.")]
    public static string LastAuthHeader() => HttpAuthCapture.LastAuthHeader ?? "(none)";

    /// <summary>
    /// HTTP-mode-only tool: returns the User-Agent of the most recent request,
    /// or '(none)'. Lets tests verify Netclaw advertises a stable agent string.
    /// </summary>
    [McpServerTool(Name = "last_user_agent")]
    [Description("Returns the User-Agent attached to the most recent incoming request, or '(none)' if no such header has been seen.")]
    public static string LastUserAgent() => HttpAuthCapture.LastUserAgent ?? "(none)";

    /// <summary>
    /// HTTP-mode-only tool: returns the X-Netclaw-Component header value of the
    /// most recent request, or '(none)'.
    /// </summary>
    [McpServerTool(Name = "last_netclaw_component")]
    [Description("Returns the X-Netclaw-Component header attached to the most recent incoming request, or '(none)'.")]
    public static string LastNetclawComponent() => HttpAuthCapture.LastNetclawComponent ?? "(none)";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = ParseArgs(args);
            return parsed.Transport switch
            {
                "http" => await RunHttpAsync(parsed),
                _ => await RunStdioAsync(),
            };
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[smoke-mcp:error] {ex}");
            return 1;
        }
    }

    private static async Task<int> RunStdioAsync()
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

    private static async Task<int> RunHttpAsync(ParsedArgs args)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Listen on 127.0.0.1 (not localhost) so `--port 0` works.
            // Kestrel rejects port 0 on the dual-stack ListenLocalhost path.
            kestrel.Listen(System.Net.IPAddress.Loopback, args.Port);
        });

        builder.Services
            .AddMcpServer(opts =>
            {
                opts.ServerInfo = new Implementation
                {
                    Name = "netclaw-smoke-mcp",
                    Version = "1.0.0",
                };
                opts.ServerInstructions =
                    "Deterministic HTTP test server for the Netclaw smoke harness. " +
                    "Includes 'last_auth_header' which echoes the Authorization header " +
                    "from the most recent request.";
            })
            .WithHttpTransport()
            .WithTools<Program>();

        var app = builder.Build();

        if (args.CaptureAuth)
        {
            app.Use(async (ctx, next) =>
            {
                HttpAuthCapture.LastAuthHeader = ctx.Request.Headers.TryGetValue("Authorization", out var v)
                    ? v.ToString()
                    : null;
                HttpAuthCapture.LastUserAgent = ctx.Request.Headers.TryGetValue("User-Agent", out var ua)
                    ? ua.ToString()
                    : null;
                HttpAuthCapture.LastNetclawComponent = ctx.Request.Headers.TryGetValue("X-Netclaw-Component", out var comp)
                    ? comp.ToString()
                    : null;
                await next();
            });
        }

        app.MapMcp("/mcp");

        await app.StartAsync();

        // Resolve the actual listening port (may differ from args.Port when 0
        // was requested) and publish it to stderr so the spawning test can
        // attach without racing on a fixed port.
        var serverAddresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var address = serverAddresses?.Addresses.FirstOrDefault() ?? $"http://127.0.0.1:{args.Port}";
        await Console.Error.WriteLineAsync($"[smoke-mcp:listening] {address.TrimEnd('/')}/mcp");

        await app.WaitForShutdownAsync();
        return 0;
    }

    private static ParsedArgs ParseArgs(string[] args)
    {
        var transport = "stdio";
        var port = 0;
        var captureAuth = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--transport" when i + 1 < args.Length:
                    transport = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--capture-auth":
                    captureAuth = true;
                    break;
            }
        }

        return new ParsedArgs(transport, port, captureAuth);
    }

    private sealed record ParsedArgs(string Transport, int Port, bool CaptureAuth);
}

/// <summary>
/// Holds the Authorization header from the most recently observed inbound
/// request. Module-static so the <c>[McpServerTool]</c> static method can
/// read it without dependency injection plumbing — the smoke server is
/// single-process by construction and the only writer is the capture
/// middleware in <see cref="Program.RunHttpAsync"/>.
/// </summary>
internal static class HttpAuthCapture
{
    public static string? LastAuthHeader { get; set; }
    public static string? LastUserAgent { get; set; }
    public static string? LastNetclawComponent { get; set; }
}
