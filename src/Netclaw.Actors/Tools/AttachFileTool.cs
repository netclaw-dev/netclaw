// -----------------------------------------------------------------------
// <copyright file="AttachFileTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Attaches a file as output to the user.
/// Paths inside the current session are attached directly. Paths from sibling
/// Netclaw session directories are copied into the current session first.
/// Interactive Personal sessions can copy another policy-authorized readable
/// source. Other callers remain limited to the session tree.
/// </summary>
[NetclawTool("attach_file",
    "Attach an existing authorized file to the user. Pass the source path directly; Netclaw copies the file into the current session when required. Do not copy the file with shell or another file tool first.",
    Grant = "file")]
public sealed partial class AttachFileTool : NetclawTool<AttachFileTool.Params>
{
    public const string ToolName = "attach_file";

    private readonly PathAccessPolicy _pathAccessPolicy;

    public record Params(
        [property: Description("Existing authorized source file path. Relative paths use the current project, then session_dir. Pass this path directly without first copying the file.")] string Path,
        [property: Description("Optional display name for the file")] string? DisplayName = null);

    public AttachFileTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
        : this(new PathAccessPolicy(config, paths, pathPolicy))
    {
    }

    internal AttachFileTool(PathAccessPolicy pathAccessPolicy)
    {
        _pathAccessPolicy = pathAccessPolicy;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return Task.FromResult(context.InvalidInput("Error: 'path' parameter is required."));

        if (string.IsNullOrWhiteSpace(context.SessionDirectory))
            return Task.FromResult(context.InvalidInput("Error: invalid_context: No session directory available."));

        var access = _pathAccessPolicy.Evaluate(args.Path, context, PathAccessPolicy.FileOperation.Attach);
        if (!access.Allowed)
            return Task.FromResult(context.PathAccessFailure(access.Error, access.Failure ?? PathAccessPolicy.PathAccessFailure.AccessDenied));

        var requestedPath = access.CanonicalPath;

        var sessionDir = PathUtility.Normalize(context.SessionDirectory);

        if (!File.Exists(requestedPath))
            return Task.FromResult(context.NotFound($"Error: File not found: {requestedPath}"));

        var resolvedPath = ResolveFinalPath(requestedPath);

        var resolvedAccess = _pathAccessPolicy.Evaluate(resolvedPath, context, PathAccessPolicy.FileOperation.Attach);
        if (!resolvedAccess.Allowed)
            return Task.FromResult(context.PathAccessFailure(resolvedAccess.Error, resolvedAccess.Failure ?? PathAccessPolicy.PathAccessFailure.AccessDenied));

        resolvedPath = resolvedAccess.CanonicalPath;
        var resolvedInCurrentSession = PathUtility.IsWithinRoot(resolvedPath, sessionDir);

        var attachPath = resolvedInCurrentSession
            ? resolvedPath
            : CopyIntoCurrentSession(resolvedPath, sessionDir);

        if (!PathUtility.IsWithinRoot(attachPath, sessionDir))
            return Task.FromResult(context.AccessDenied($"Error: Attach path escaped the session directory ({sessionDir})."));

        var rawFilename = args.DisplayName ?? Path.GetFileName(attachPath);
        var sanitizedFilename = FilenameSanitizer.Sanitize(rawFilename);
        var mimeType = MimeTypeCatalog.FromPathExtension(attachPath) ?? MimeType.Default;

        context.Outputs.AddFileAttachment(attachPath, sanitizedFilename, mimeType);
        var copiedText = string.Equals(attachPath, resolvedPath, StringComparison.Ordinal)
            ? string.Empty
            : " (copied into current session)";

        return Task.FromResult(context.SuccessFile(
            $"File attached: {sanitizedFilename} ({mimeType}) at {attachPath}{copiedText}",
            resolvedPath,
            ToolFileActivityKind.Read));
    }

    private static string ResolveFinalPath(string path)
    {
        var fileInfo = new FileInfo(path);
        var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? fileInfo.FullName : target.FullName;
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
