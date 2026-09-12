// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace Netclaw.SmokeLlmServer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            await using var app = await SmokeLlmServerHost.StartAsync(options);
            await Console.Error.WriteLineAsync($"[smoke-llm:listening] {SmokeLlmServerHost.GetBaseAddress(app)}");
            await app.WaitForShutdownAsync();
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[smoke-llm:error] {ex.Message}");
            return 1;
        }
    }

    private static SmokeLlmServerOptions ParseOptions(string[] args)
    {
        int? port = null;
        string? requestRecordPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--port" when index + 1 < args.Length:
                    port = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--request-record" when index + 1 < args.Length:
                    requestRecordPath = args[++index];
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument '{args[index]}'.");
            }
        }

        if (port is null)
            throw new ArgumentException("The --port argument is required.");
        if (string.IsNullOrWhiteSpace(requestRecordPath))
            throw new ArgumentException("The --request-record argument is required.");

        return new SmokeLlmServerOptions(port.Value, requestRecordPath);
    }
}

public sealed record SmokeLlmServerOptions(int Port, string RequestRecordPath, IPAddress? Address = null)
{
    public const string ModelId = "netclaw-smoke-tool-model";

    public IPAddress BindAddress => Address ?? IPAddress.Loopback;
}

public static class SmokeLlmServerHost
{
    public const string SkillPluginProofPrompt = "NETCLAW_SMOKE_SKILL_PLUGIN_PROOF";
    public const string SkillPluginProofResponse = "Skill plugin proof passed.";
    private const string ProofSkillName = "akka-net-best-practices";
    private const string ProofResourcePath = "cluster-local-abstractions.md";

    public static async Task<WebApplication> StartAsync(
        SmokeLlmServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "The port must be between 0 and 65535.");
        if (!IPAddress.Loopback.Equals(options.BindAddress))
            throw new ArgumentException("The smoke LLM server must bind to 127.0.0.1.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.RequestRecordPath))
            throw new ArgumentException("The request record path is required.", nameof(options));

        var requestRecorder = new RequestRecorder(options.RequestRecordPath);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

        var app = builder.Build();
        var skillFeed = new SkillFeedFixture();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/v1/models", () => Results.Ok(new
        {
            @object = "list",
            data = new[]
            {
                new
                {
                    id = SmokeLlmServerOptions.ModelId,
                    @object = "model",
                    created = 0,
                    owned_by = "netclaw-smoke"
                }
            }
        }));
        app.MapPost("/v1/chat/completions", context => HandleCompletionAsync(context, requestRecorder));
        app.MapPost("/test/skill-feed/phase/{phase}", (string phase) =>
            skillFeed.SetPhase(phase)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "The phase must be A or B." }));
        app.MapGet("/test/skill-feed/.well-known/agent-skills/index.json", (HttpRequest request) =>
            Results.Json(skillFeed.CreateIndex(request)));
        app.MapGet("/test/skill-feed/skill.md", () =>
            Results.Text(skillFeed.GetSkill(), "text/markdown", Encoding.UTF8));
        app.MapGet("/test/skill-feed/proof.txt", () =>
            Results.Text(skillFeed.GetResource(), "text/plain", Encoding.UTF8));

        await app.StartAsync(cancellationToken);
        return app;
    }

    public static string GetBaseAddress(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?.Addresses;
        return addresses?.SingleOrDefault() ?? throw new InvalidOperationException("The smoke LLM server did not publish a listening address.");
    }

    private static async Task HandleCompletionAsync(HttpContext context, RequestRecorder requestRecorder)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context.Response, StatusCodes.Status400BadRequest, "Request body must be valid JSON.");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                await WriteErrorAsync(context.Response, StatusCodes.Status400BadRequest, "Request body must be a JSON object.");
                return;
            }

            var model = GetStringProperty(root, "model");
            var stream = root.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind is JsonValueKind.True;
            var toolsPresent = root.TryGetProperty("tools", out var toolsValue) && toolsValue.ValueKind is JsonValueKind.Array;
            await requestRecorder.RecordAsync(new SmokeRequestRecord("/v1/chat/completions", model, stream, toolsPresent), context.RequestAborted);

            if (model is not { } knownModel || !string.Equals(knownModel, SmokeLlmServerOptions.ModelId, StringComparison.Ordinal))
            {
                await WriteErrorAsync(
                    context.Response,
                    StatusCodes.Status400BadRequest,
                    $"Unknown model '{model ?? "(missing)"}'. Use '{SmokeLlmServerOptions.ModelId}'.");
                return;
            }

            if (stream)
            {
                if (IsSkillPluginProofRequest(root))
                {
                    await WriteSkillPluginProofAsync(context.Response, knownModel, root);
                    return;
                }

                await WriteStreamingCompletionAsync(context.Response, knownModel);
                return;
            }

            await context.Response.WriteAsJsonAsync(new
            {
                id = "chatcmpl-netclaw-smoke",
                @object = "chat.completion",
                created = 0,
                model = knownModel,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "Netclaw smoke response." },
                        finish_reason = "stop"
                    }
                }
            }, cancellationToken: context.RequestAborted);
        }
    }

    private static async Task WriteStreamingCompletionAsync(HttpResponse response, string model)
        => await WriteStreamingTextAsync(response, model, "Netclaw smoke response.");

    private static async Task WriteSkillPluginProofAsync(
        HttpResponse response,
        string model,
        JsonElement root)
    {
        var toolResults = GetToolResults(root);
        if (toolResults.Count == 0)
        {
            if (!HasTool(root, "skill_load"))
            {
                await WriteStreamingTextAsync(response, model, "Skill plugin proof failed: skill_load is unavailable.");
                return;
            }

            await WriteStreamingToolCallAsync(
                response,
                model,
                "call_skill_load",
                "skill_load",
                JsonSerializer.Serialize(new
                {
                    Name = ProofSkillName,
                    _rationale = "Load the installed skill for the public plugin proof.",
                }));
            return;
        }

        if (toolResults.Count == 1)
        {
            if (!toolResults[0].Contains("# Akka.NET Best Practices", StringComparison.Ordinal)
                || !toolResults[0].Contains(ProofResourcePath, StringComparison.Ordinal))
            {
                await WriteStreamingTextAsync(response, model, "Skill plugin proof failed: skill_load returned unexpected content.");
                return;
            }
            if (!HasTool(root, "skill_read_resource"))
            {
                await WriteStreamingTextAsync(response, model, "Skill plugin proof failed: skill_read_resource is unavailable.");
                return;
            }

            await WriteStreamingToolCallAsync(
                response,
                model,
                "call_skill_resource",
                "skill_read_resource",
                JsonSerializer.Serialize(new
                {
                    SkillName = ProofSkillName,
                    ResourcePath = ProofResourcePath,
                    _rationale = "Read the bundled resource for the public plugin proof.",
                }));
            return;
        }

        var resource = toolResults[^1];
        var result = resource.StartsWith("# Cluster/Local Mode Abstractions", StringComparison.Ordinal)
            && resource.Contains("GenericChildPerEntityParent", StringComparison.Ordinal)
                ? SkillPluginProofResponse
                : "Skill plugin proof failed: skill_read_resource returned unexpected content.";
        await WriteStreamingTextAsync(response, model, result);
    }

    private static bool IsSkillPluginProofRequest(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages)
            || messages.ValueKind is not JsonValueKind.Array)
            return false;

        foreach (var message in messages.EnumerateArray())
        {
            if (GetStringProperty(message, "role") == "user"
                && GetStringProperty(message, "content") == SkillPluginProofPrompt)
                return true;
        }

        return false;
    }

    private static List<string> GetToolResults(JsonElement root)
    {
        var results = new List<string>();
        if (!root.TryGetProperty("messages", out var messages)
            || messages.ValueKind is not JsonValueKind.Array)
            return results;

        foreach (var message in messages.EnumerateArray())
        {
            if (GetStringProperty(message, "role") == "tool"
                && GetStringProperty(message, "content") is { } content)
                results.Add(content);
        }

        return results;
    }

    private static bool HasTool(JsonElement root, string name)
    {
        if (!root.TryGetProperty("tools", out var tools)
            || tools.ValueKind is not JsonValueKind.Array)
            return false;

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.TryGetProperty("function", out var function)
                && GetStringProperty(function, "name") == name)
                return true;
        }

        return false;
    }

    private static async Task WriteStreamingTextAsync(
        HttpResponse response,
        string model,
        string content)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        await WriteEventAsync(response, new
        {
            id = "chatcmpl-netclaw-smoke",
            @object = "chat.completion.chunk",
            created = 0,
            model,
            choices = new[]
            {
                new { index = 0, delta = new { role = "assistant", content }, finish_reason = (string?)null }
            }
        });
        await WriteEventAsync(response, new
        {
            id = "chatcmpl-netclaw-smoke",
            @object = "chat.completion.chunk",
            created = 0,
            model,
            choices = new[]
            {
                new { index = 0, delta = new { }, finish_reason = "stop" }
            }
        });
        await response.WriteAsync("data: [DONE]\n\n");
        await response.Body.FlushAsync();
    }

    private static async Task WriteStreamingToolCallAsync(
        HttpResponse response,
        string model,
        string callId,
        string name,
        string arguments)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        await WriteEventAsync(response, new
        {
            id = "chatcmpl-netclaw-smoke",
            @object = "chat.completion.chunk",
            created = 0,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id = callId,
                                type = "function",
                                function = new { name, arguments },
                            },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        });
        await WriteEventAsync(response, new
        {
            id = "chatcmpl-netclaw-smoke",
            @object = "chat.completion.chunk",
            created = 0,
            model,
            choices = new[]
            {
                new { index = 0, delta = new { }, finish_reason = "tool_calls" },
            },
        });
        await response.WriteAsync("data: [DONE]\n\n");
        await response.Body.FlushAsync();
    }

    private static async Task WriteEventAsync(HttpResponse response, object value)
    {
        var json = JsonSerializer.Serialize(value);
        await response.WriteAsync($"data: {json}\n\n", Encoding.UTF8);
        await response.Body.FlushAsync();
    }

    private static Task WriteErrorAsync(HttpResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        return response.WriteAsJsonAsync(new { error = new { message } });
    }

    private static string? GetStringProperty(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed class SkillFeedFixture
{
    private const string FeedPath = "/test/skill-feed";
    private string _phase = "A";

    public bool SetPhase(string phase)
    {
        if (phase is not ("A" or "B"))
            return false;

        Volatile.Write(ref _phase, phase);
        return true;
    }

    public object CreateIndex(HttpRequest request)
    {
        var phase = Volatile.Read(ref _phase);
        var version = GetVersion(phase);
        var skill = GetSkill(phase);
        var resource = GetResource(phase);
        var baseUrl = $"{request.Scheme}://{request.Host}{FeedPath}";

        return new
        {
            skills = new[]
            {
                new
                {
                    name = "smoke-feed-skill",
                    type = "skill",
                    description = "Native skill sync smoke proof",
                    url = $"{baseUrl}/skill.md",
                    digest = $"sha256:{GetDigest(skill)}",
                    version,
                    resources = new[]
                    {
                        new
                        {
                            path = "references/proof.txt",
                            url = $"{baseUrl}/proof.txt",
                            digest = $"sha256:{GetDigest(resource)}"
                        }
                    }
                }
            }
        };
    }

    public string GetSkill() => GetSkill(Volatile.Read(ref _phase));

    public string GetResource() => GetResource(Volatile.Read(ref _phase));

    private static string GetVersion(string phase) => phase == "A" ? "1.0.0" : "2.0.0";

    private static string GetSkill(string phase) =>
        $"---\nname: smoke-feed-skill\ndescription: Native skill sync phase {phase}.\nmetadata:\n  version: \"{GetVersion(phase)}\"\n---\n\n# Native skill sync phase {phase}\n";

    private static string GetResource(string phase) => $"native skill sync resource phase {phase}\n";

    private static string GetDigest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

public sealed record SmokeRequestRecord(string Route, string? Model, bool Stream, bool ToolsPresent);

internal sealed class RequestRecorder
{
    private const int MaxRecords = 128;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _recordPath;
    private int _recordCount;

    public RequestRecorder(string recordPath)
    {
        _recordPath = Path.GetFullPath(recordPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath) ?? throw new InvalidOperationException("The request record path has no directory."));
    }

    public async Task RecordAsync(SmokeRequestRecord record, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _recordCount) > MaxRecords)
            return;

        var line = JsonSerializer.Serialize(record) + Environment.NewLine;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_recordPath, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
