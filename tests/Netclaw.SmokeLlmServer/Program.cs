// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
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
                new { index = 0, delta = new { role = "assistant", content = "Netclaw smoke response." }, finish_reason = (string?)null }
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
