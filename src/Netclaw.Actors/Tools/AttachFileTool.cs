// -----------------------------------------------------------------------
// <copyright file="AttachFileTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Configuration;
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
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("Absolute path to the file to attach")] string Path,
        [property: Description("Optional display name for the file")] string? DisplayName = null);

    public AttachFileTool()
        : this(new ToolConfig())
    {
    }

    public AttachFileTool(ToolConfig config)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(config);
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => Task.FromResult("Error: attach_file requires a session context.");

    protected override Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return Task.FromResult("Error: 'path' parameter is required.");

        if (string.IsNullOrWhiteSpace(context.SessionDirectory))
            return Task.FromResult("Error: No session directory available.");

        if (!_fileAccessPolicy.TryResolveAttachPath(args.Path, context, out var requestedPath, out var accessError))
            return Task.FromResult(accessError);

        var sessionDir = PathUtility.Normalize(context.SessionDirectory);
        var sessionRoot = TryGetSessionRootDirectory(sessionDir);

        var requestedInCurrentSession = PathUtility.IsWithinRoot(requestedPath, sessionDir);
        var requestedInSessionRoot = sessionRoot is not null && PathUtility.IsWithinRoot(requestedPath, sessionRoot);

        if (!requestedInCurrentSession && !requestedInSessionRoot)
        {
            return Task.FromResult(
                $"Error: File path must be within the current session directory ({sessionDir}) or another Netclaw session under {sessionRoot ?? "<unknown>"}.");
        }

        if (!File.Exists(requestedPath))
            return Task.FromResult($"Error: File not found: {requestedPath}");

        var resolvedPath = ResolveFinalPath(requestedPath);
        var resolvedInCurrentSession = PathUtility.IsWithinRoot(resolvedPath, sessionDir);
        var resolvedInSessionRoot = sessionRoot is not null && PathUtility.IsWithinRoot(resolvedPath, sessionRoot);

        if (!resolvedInCurrentSession && !resolvedInSessionRoot)
        {
            return Task.FromResult(
                $"Error: File path must be within the current session directory ({sessionDir}) or another Netclaw session under {sessionRoot ?? "<unknown>"}.");
        }

        var attachPath = resolvedInCurrentSession
            ? resolvedPath
            : CopyIntoCurrentSession(resolvedPath, sessionDir);

        if (!PathUtility.IsWithinRoot(attachPath, sessionDir))
            return Task.FromResult($"Error: Attach path escaped the session directory ({sessionDir}).");

        var rawFilename = args.DisplayName ?? Path.GetFileName(attachPath);
        var sanitizedFilename = FilenameSanitizer.Sanitize(rawFilename);
        var mimeType = MimeTypeCatalog.FromPathExtension(attachPath) ?? MimeTypeCatalog.ApplicationOctetStream;

        context.AddFileAttachment(attachPath, sanitizedFilename, mimeType);
        var copiedText = string.Equals(attachPath, resolvedPath, StringComparison.Ordinal)
            ? string.Empty
            : " (copied into current session)";

        return Task.FromResult($"File attached: {sanitizedFilename} ({mimeType}) at {attachPath}{copiedText}");
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
            return PathUtility.Normalize(parent.FullName);
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

}
