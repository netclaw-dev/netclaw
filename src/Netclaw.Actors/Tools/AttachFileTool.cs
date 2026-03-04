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

        var sessionDir = NormalizeDirectoryPath(context.SessionDirectory);
        var requestedPath = Path.GetFullPath(args.Path);

        if (!IsPathWithinDirectory(requestedPath, sessionDir))
            return Task.FromResult($"Error: File path must be within the session directory ({sessionDir}).");

        if (!File.Exists(requestedPath))
            return Task.FromResult($"Error: File not found: {requestedPath}");

        var resolvedPath = ResolveFinalPath(requestedPath);
        if (!IsPathWithinDirectory(resolvedPath, sessionDir))
            return Task.FromResult($"Error: File path must be within the session directory ({sessionDir}).");

        var rawFilename = args.DisplayName ?? Path.GetFileName(resolvedPath);
        var sanitizedFilename = FilenameSanitizer.Sanitize(rawFilename);
        var mimeType = GuessMimeType(resolvedPath);

        context.AddFileAttachment(resolvedPath, sanitizedFilename, mimeType);
        return Task.FromResult($"File attached: {sanitizedFilename} ({mimeType}) at {resolvedPath}");
    }

    private static string NormalizeDirectoryPath(string directoryPath)
    {
        return Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            return false;

        if (fullPath.Length == directory.Length)
            return true;

        var boundary = fullPath[directory.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private static string ResolveFinalPath(string path)
    {
        var fileInfo = new FileInfo(path);
        var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? fileInfo.FullName : target.FullName;
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
