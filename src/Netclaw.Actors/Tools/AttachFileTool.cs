using System.ComponentModel;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Attaches a file from the session directory as output to the user.
/// Validates that the file path is within the session directory to prevent traversal.
/// </summary>
[NetclawTool("attach_file",
    "Attach a file from the session directory to send to the user. The path must be within the session working directory.",
    Grant = "file")]
public sealed partial class AttachFileTool : NetclawTool<AttachFileTool.Params>
{
    public record Params(
        [property: Description("Absolute path to the file to attach")] string Path,
        [property: Description("Optional display name for the file")] string? DisplayName = null);

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => Task.FromResult("Error: attach_file requires a session context.");

    protected override Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return Task.FromResult("Error: 'path' parameter is required.");

        if (string.IsNullOrWhiteSpace(context.SessionDirectory))
            return Task.FromResult("Error: No session directory available.");

        // Canonicalize both paths to prevent traversal attacks
        var sessionDir = Path.GetFullPath(context.SessionDirectory);
        var filePath = Path.GetFullPath(args.Path);

        if (!filePath.StartsWith(sessionDir, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult($"Error: File path must be within the session directory ({sessionDir}).");

        if (!File.Exists(filePath))
            return Task.FromResult($"Error: File not found: {filePath}");

        var rawFilename = args.DisplayName ?? Path.GetFileName(filePath);
        var sanitizedFilename = FilenameSanitizer.Sanitize(rawFilename);
        var mimeType = GuessMimeType(filePath);

        return Task.FromResult($"File attached: {sanitizedFilename} ({mimeType}) at {filePath}");
    }

    private static string GuessMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".md" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };
    }
}
