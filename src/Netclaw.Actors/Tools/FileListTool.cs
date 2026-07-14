// -----------------------------------------------------------------------
// <copyright file="FileListTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Lists the immediate entries of a directory. Read-only: it never reads file
/// contents and never recurses into subdirectories. The target directory is
/// authorized through <see cref="ScopedFileAccessPolicy"/> read access, so the
/// directories an audience may list are exactly that audience's read roots.
/// </summary>
[NetclawTool(ToolName,
    "List the files and subdirectories directly inside a directory. Returns a single-level listing only — it does not read file contents and does not recurse.",
    Grant = "file")]
public sealed partial class FileListTool : NetclawTool<FileListTool.Params>
{
    public const string ToolName = "file_list";

    private const int MaxEntries = 1000;

    private readonly ScopedFileAccessPolicy _fileAccessPolicy;
    private readonly ToolPathPolicy _pathPolicy;

    public record Params(
        [property: Description("Absolute path to the directory to list")] string Path);

    public FileListTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
        _pathPolicy = pathPolicy;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return Task.FromResult("Error: 'path' parameter is required.");

        // Authorize through the read-access policy: this confines the listable
        // directories to the audience's read roots and emits an
        // audience-sanitized error (no configured root paths leaked to Public).
        if (!_fileAccessPolicy.TryResolveReadPath(args.Path, context, out var authorizedPath, out var accessError))
            return Task.FromResult(accessError);

        if (_pathPolicy.IsReadDenied(authorizedPath))
            return Task.FromResult(FileToolErrors.CredentialReadDenied(authorizedPath));

        if (!Directory.Exists(authorizedPath))
        {
            return Task.FromResult(File.Exists(authorizedPath)
                ? $"Error: Not a directory (use file_read for files): {authorizedPath}"
                : $"Error: Directory not found: {authorizedPath}");
        }

        try
        {
            return Task.FromResult(FormatListing(authorizedPath, _pathPolicy));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult($"Error: Permission denied: {authorizedPath}");
        }
        catch (IOException ex)
        {
            return Task.FromResult($"Error listing directory: {ex.Message}");
        }
    }

    private static string FormatListing(string directory, ToolPathPolicy? pathPolicy)
    {
        var dirs = Directory.EnumerateDirectories(directory)
            .Where(path => pathPolicy?.IsReadDenied(path) != true)
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = Directory.EnumerateFiles(directory)
            .Where(path => pathPolicy?.IsReadDenied(path) != true)
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = dirs.Count + files.Count;
        if (total == 0)
            return $"Directory is empty: {directory}";

        var sb = new StringBuilder();
        sb.Append($"Directory listing for {directory} ({total} entr{(total == 1 ? "y" : "ies")}):");

        var shown = 0;
        foreach (var name in dirs)
        {
            if (shown >= MaxEntries)
                break;
            sb.Append($"\n[dir]  {name}/");
            shown++;
        }

        foreach (var name in files)
        {
            if (shown >= MaxEntries)
                break;
            sb.Append($"\n[file] {name}");
            shown++;
        }

        if (total > MaxEntries)
            sb.Append($"\n[listing truncated — showing {MaxEntries} of {total} entries]");

        return sb.ToString();
    }
}
