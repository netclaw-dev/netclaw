// -----------------------------------------------------------------------
// <copyright file="ToolOutputReadTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool(ToolName,
    "Continue a truncated tool result from this session by opaque call id. Returns at most Limit characters from Start and never accepts a file path.",
    Grant = "builtin")]
internal sealed partial class ToolOutputReadTool : NetclawTool<ToolOutputReadTool.Params>
{
    public const string ToolName = "tool_output_read";
    internal const int DefaultLimit = 8_000;
    internal const int MinimumLimit = 128;
    internal const int MaximumLimit = 10_000;
    internal const int MaximumStart = 256_000;
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public record Params(
        [property: Description("Opaque call id shown by the truncated tool result. Never pass a file path.")]
        string CallId,
        [property: Description("Zero-based character offset (default 0, maximum 256000).")] int? Start = null,
        [property: Description("Maximum total characters returned, including continuation metadata (default 8000, minimum 128, maximum 10000).")] int? Limit = null);

    protected override async Task<string> ExecuteAsync(
        Params args,
        ToolInvocationContext context,
        CancellationToken ct)
    {
        if (!ToolOutputSpillLocation.TryResolve(
                context.SessionDirectory,
                args.CallId,
                out _,
                out var path))
        {
            return context.InvalidInput(
                "Error: CallId must be an opaque identifier from a truncated tool result in this session.");
        }

        var start = args.Start ?? 0;
        if (start is < 0 or > MaximumStart)
            return context.InvalidInput($"Error: Start must be between 0 and {MaximumStart}.");

        var limit = args.Limit ?? DefaultLimit;
        if (limit is < MinimumLimit or > MaximumLimit)
            return context.InvalidInput($"Error: Limit must be between {MinimumLimit} and {MaximumLimit}.");

        if (!ToolOutputSpillLocation.IsSafeForIo(context.SessionDirectory!, path))
            return context.AccessDenied("Error: Retained output path is not safe to access.");

        if (!File.Exists(path))
        {
            return context.NotFound(
                "Error: No retained output exists for that call in this session. Re-run the source tool with narrower output bounds.");
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: false);

            var remaining = start;
            var buffer = new char[Math.Min(4096, Math.Max(limit, 1))];
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    ct);
                if (read == 0)
                    return context.InvalidInput("Error: Start exceeds the retained output length.");
                remaining -= read;
            }

            var captured = new StringBuilder(limit + 1);
            while (captured.Length <= limit)
            {
                ct.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - captured.Length)),
                    ct);
                if (read == 0)
                    break;
                captured.Append(buffer, 0, read);
            }

            return context.Success(ComposeWindow(captured, start, limit));
        }
        catch (DecoderFallbackException)
        {
            return context.InvalidInput("Error: Retained output is not valid UTF-8.");
        }
        catch (UnauthorizedAccessException)
        {
            return context.AccessDenied("Error: Retained output is not accessible.");
        }
        catch (FileNotFoundException)
        {
            return context.NotFound("Error: Retained output no longer exists. Re-run the source tool with narrower output bounds.");
        }
        catch (DirectoryNotFoundException)
        {
            return context.NotFound("Error: Retained output no longer exists. Re-run the source tool with narrower output bounds.");
        }
        catch (IOException ex)
        {
            return context.TransientFailure($"Error reading retained output: {ex.Message}");
        }
    }

    private static string ComposeWindow(StringBuilder captured, int start, int limit)
    {
        var reachedEnd = captured.Length <= limit;
        var contentLength = Math.Min(captured.Length, limit);
        string metadata;

        while (true)
        {
            var complete = reachedEnd && contentLength == captured.Length;
            var next = complete ? "none" : (start + contentLength).ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata = $"\n[range start={start} end={start + contentLength}; next_start={next}; complete={complete.ToString().ToLowerInvariant()}]";
            var boundedContentLength = Math.Min(captured.Length, limit - metadata.Length);
            if (boundedContentLength >= contentLength)
                break;
            contentLength = boundedContentLength;
        }

        return captured.ToString(0, contentLength) + metadata;
    }
}
