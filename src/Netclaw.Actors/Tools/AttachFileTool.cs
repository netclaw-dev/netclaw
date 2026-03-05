using System.ComponentModel;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Attaches a file as output to the user.
/// Paths inside the current session are attached directly. Paths from sibling
/// Netclaw session directories are copied into the current session first.
/// All other paths are rejected to prevent traversal/exfiltration.
/// </summary>
[NetclawTool("attach_file",
    "Attach a file to send to the user. Paths in the current session attach directly; files from other Netclaw session folders are copied into this session first.",
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
        var sessionRoot = TryGetSessionRootDirectory(sessionDir);
        var requestedPath = Path.GetFullPath(args.Path);

        var requestedInCurrentSession = IsPathWithinDirectory(requestedPath, sessionDir);
        var requestedInSessionRoot = sessionRoot is not null && IsPathWithinDirectory(requestedPath, sessionRoot);

        if (!requestedInCurrentSession && !requestedInSessionRoot)
        {
            return Task.FromResult(
                $"Error: File path must be within the current session directory ({sessionDir}) or another Netclaw session under {sessionRoot ?? "<unknown>"}.");
        }

        if (!File.Exists(requestedPath))
            return Task.FromResult($"Error: File not found: {requestedPath}");

        var resolvedPath = ResolveFinalPath(requestedPath);
        var resolvedInCurrentSession = IsPathWithinDirectory(resolvedPath, sessionDir);
        var resolvedInSessionRoot = sessionRoot is not null && IsPathWithinDirectory(resolvedPath, sessionRoot);

        if (!resolvedInCurrentSession && !resolvedInSessionRoot)
        {
            return Task.FromResult(
                $"Error: File path must be within the current session directory ({sessionDir}) or another Netclaw session under {sessionRoot ?? "<unknown>"}.");
        }

        var attachPath = resolvedInCurrentSession
            ? resolvedPath
            : CopyIntoCurrentSession(resolvedPath, sessionDir);

        if (!IsPathWithinDirectory(attachPath, sessionDir))
            return Task.FromResult($"Error: Attach path escaped the session directory ({sessionDir}).");

        var rawFilename = args.DisplayName ?? Path.GetFileName(attachPath);
        var sanitizedFilename = FilenameSanitizer.Sanitize(rawFilename);
        var mimeType = GuessMimeType(attachPath);

        context.AddFileAttachment(attachPath, sanitizedFilename, mimeType);
        var copiedText = string.Equals(attachPath, resolvedPath, StringComparison.Ordinal)
            ? string.Empty
            : " (copied into current session)";

        return Task.FromResult($"File attached: {sanitizedFilename} ({mimeType}) at {attachPath}{copiedText}");
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

    private static string? TryGetSessionRootDirectory(string sessionDir)
    {
        var parent = Directory.GetParent(sessionDir);
        if (parent is null)
            return null;

        var name = parent.Name;
        if (name.Equals("sessions", StringComparison.OrdinalIgnoreCase)
            || name.Equals("netclaw-sessions", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeDirectoryPath(parent.FullName);
        }

        return null;
    }

    private static string CopyIntoCurrentSession(string sourcePath, string sessionDir)
    {
        var attachmentsDir = Path.Combine(sessionDir, "attachments");
        Directory.CreateDirectory(attachmentsDir);

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var sanitizedBase = FilenameSanitizer.Sanitize(baseName);
        var fileName = sanitizedBase + extension;
        var destination = Path.Combine(attachmentsDir, fileName);
        var suffix = 1;

        while (File.Exists(destination))
        {
            fileName = $"{sanitizedBase}-{suffix}{extension}";
            destination = Path.Combine(attachmentsDir, fileName);
            suffix++;
        }

        File.Copy(sourcePath, destination);
        return destination;
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
