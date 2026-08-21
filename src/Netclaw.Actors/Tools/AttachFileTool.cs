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
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;
    private readonly ToolPathPolicy _pathPolicy;

    public record Params(
        [property: Description("Existing authorized source file path. Relative paths use the current project, then session scratch. Pass this path directly without first copying the file.")] string Path,
        [property: Description("Optional display name for the file")] string? DisplayName = null);

    public AttachFileTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
        _pathPolicy = pathPolicy;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return Task.FromResult(context.InvalidInput("Error: 'path' parameter is required."));

        if (string.IsNullOrWhiteSpace(context.SessionDirectory))
            return Task.FromResult(context.InvalidInput("Error: invalid_context: No session directory available."));

        if (!_fileAccessPolicy.TryResolveAttachPath(
                args.Path,
                context,
                out var requestedPath,
                out var accessError,
                out var resolutionFailure))
        {
            return Task.FromResult(context.PathResolutionFailure(accessError, resolutionFailure));
        }

        // Same hard-deny surface as file_read/file_list: attach must never ship
        // control-plane files (secrets, keys, webhooks, config, sqlite, pid,
        // lock, restart manifest) that shell cannot even reference (#1724).
        if (_pathPolicy.IsReadDenied(requestedPath))
            return Task.FromResult(context.AccessDenied(FileToolErrors.CredentialReadDenied(requestedPath)));

        var sessionDir = PathUtility.Normalize(context.SessionDirectory);
        var sessionRoot = TryGetSessionRootDirectory(sessionDir);

        // Interactive Personal-audience sessions get shell-equivalent reach:
        // shell can attach anything it can read, so the session-proximity
        // restriction is lifted for them. The out-of-session file is still
        // copied into this session's attachments directory below, preserving
        // delivery semantics. Non-interactive, Team, and Public sessions keep
        // the proximity gate.
        var interactivePersonalReach = ScopedFileAccessPolicy.HasInteractivePersonalReach(context);

        var requestedInCurrentSession = PathUtility.IsWithinRoot(requestedPath, sessionDir);
        var requestedInSessionRoot = sessionRoot is not null && PathUtility.IsWithinRoot(requestedPath, sessionRoot);

        if (!interactivePersonalReach && !requestedInCurrentSession && !requestedInSessionRoot)
        {
            return Task.FromResult(context.AccessDenied(
                $"Error: File path must be within the current session directory ({sessionDir}) or another Netclaw session under {sessionRoot ?? "<unknown>"}."));
        }

        if (!File.Exists(requestedPath))
            return Task.FromResult(context.NotFound($"Error: File not found: {requestedPath}"));

        var resolvedPath = ResolveFinalPath(requestedPath);

        // Defense-in-depth: re-check the deny against the symlink-resolved
        // target so any future divergence between requestedPath and resolvedPath
        // cannot widen attach's surface.
        if (_pathPolicy.IsReadDenied(resolvedPath))
            return Task.FromResult(context.AccessDenied(FileToolErrors.CredentialReadDenied(resolvedPath)));

        var resolvedInCurrentSession = PathUtility.IsWithinRoot(resolvedPath, sessionDir);
        var resolvedInSessionRoot = sessionRoot is not null && PathUtility.IsWithinRoot(resolvedPath, sessionRoot);

        if (!interactivePersonalReach && !resolvedInCurrentSession && !resolvedInSessionRoot)
        {
            return Task.FromResult(context.AccessDenied(
                $"Error: File path must be within the current session directory ({sessionDir}) or another Netclaw session under {sessionRoot ?? "<unknown>"}."));
        }

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
