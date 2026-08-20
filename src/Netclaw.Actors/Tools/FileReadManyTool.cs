// -----------------------------------------------------------------------
// <copyright file="FileReadManyTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool(ToolName,
    "Read several known text files atomically without shell. Every path is authorized before any content is returned.",
    Grant = "file")]
public sealed partial class FileReadManyTool : NetclawTool<FileReadManyTool.Params>
{
    public const string ToolName = "file_read_many";
    internal const int MaximumPathCount = 32;
    internal const int MaximumCharsPerFile = 128_000;
    internal const int MaximumTotalChars = 256_000;
    private const int DefaultCharsPerFile = 16_000;
    private const int DefaultTotalChars = 64_000;

    private readonly ToolPathPolicy _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("File paths to read. Relative paths use the current project, then session scratch.")]
        string[] Paths,
        [property: Description("Maximum characters returned from each file (default 16000, maximum 128000).")] int? MaxCharsPerFile = null,
        [property: Description("Maximum characters returned across the entire result (default 64000, maximum 256000).")] int? MaxTotalChars = null);

    public FileReadManyTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
    {
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (args.Paths is not { Length: > 0 and <= MaximumPathCount })
            return context.InvalidInput($"Error: 'Paths' must contain between 1 and {MaximumPathCount} entries.");

        if (!WorkspaceFileToolSupport.TryResolveBound(
                args.MaxCharsPerFile,
                DefaultCharsPerFile,
                MaximumCharsPerFile,
                nameof(args.MaxCharsPerFile),
                out var perFileLimit,
                out var perFileError))
        {
            return context.InvalidInput(perFileError);
        }

        if (!WorkspaceFileToolSupport.TryResolveBound(
                args.MaxTotalChars,
                DefaultTotalChars,
                MaximumTotalChars,
                nameof(args.MaxTotalChars),
                out var totalLimit,
                out var totalError))
        {
            return context.InvalidInput(totalError);
        }

        var paths = new List<string>(args.Paths.Length);
        var uniquePaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var authoredPath in args.Paths)
        {
            if (string.IsNullOrWhiteSpace(authoredPath))
                return context.InvalidInput("Error: 'Paths' may not contain an empty path.");

            if (!_fileAccessPolicy.TryResolveReadPath(
                    authoredPath,
                    context,
                    out var path,
                    out var accessError,
                    out var resolutionFailure))
            {
                return context.PathResolutionFailure(accessError, resolutionFailure);
            }

            if (_pathPolicy.IsReadDenied(path))
                return context.AccessDenied(FileToolErrors.CredentialReadDenied(path));

            if (!File.Exists(path))
                return context.NotFound($"Error: File not found: {path}");

            if (!uniquePaths.Add(path))
                return context.InvalidInput($"Error: duplicate file path resolves to {path}.");

            paths.Add(path);
        }

        var prefixes = paths
            .Select((path, index) => $"{(index == 0 ? string.Empty : "\n")}== {path} ==\n")
            .ToArray();
        var prefixChars = prefixes.Sum(static prefix => prefix.Length);
        if (prefixChars + paths.Count > totalLimit)
        {
            return context.InvalidInput(
                $"Error: 'MaxTotalChars' must leave room for {paths.Count} labeled file sections.");
        }

        try
        {
            var result = new StringBuilder(Math.Min(totalLimit, prefixChars + perFileLimit * paths.Count));
            var remainingContentChars = totalLimit - prefixChars;
            for (var index = 0; index < paths.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var remainingFiles = paths.Count - index;
                var contentLimit = Math.Min(perFileLimit, remainingContentChars / remainingFiles);
                var read = await WorkspaceFileToolSupport.ReadUtf8CharsAsync(paths[index], contentLimit, ct);
                var content = AddTruncationMarker(read, contentLimit);

                result.Append(prefixes[index]);
                result.Append(content);
                remainingContentChars -= content.Length;
            }

            return context.SuccessFiles(
                result.ToString(),
                paths,
                ToolFileActivityKind.Read);
        }
        catch (DecoderFallbackException)
        {
            return context.InvalidInput("Error: file_read_many accepts UTF-8 text files only.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return context.AccessDenied($"Error: Permission denied: {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            return context.NotFound($"Error: File not found: {ex.FileName ?? ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            return context.NotFound($"Error: Directory not found: {ex.Message}");
        }
        catch (IOException ex)
        {
            return context.TransientFailure($"Error reading files: {ex.Message}");
        }
    }

    private static string AddTruncationMarker(
        WorkspaceFileToolSupport.BoundedText read,
        int maxChars)
    {
        const string marker = "\n[truncated]";
        if (!read.Truncated || maxChars < marker.Length)
            return read.Content;

        return read.Content[..Math.Min(read.Content.Length, maxChars - marker.Length)] + marker;
    }
}
