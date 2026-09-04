// -----------------------------------------------------------------------
// <copyright file="FileEditTool.cs" company="Petabridge, LLC">
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
/// Edits files: targeted text replacement (OldString → NewString) or full content
/// write (Content alone). When Content is supplied without OldString, the file is
/// created or overwritten — this subsumes the former <c>file_write</c> tool.
/// </summary>
[NetclawTool(ToolName,
    "Use for changing a known local file without shell. " +
    "Apply targeted text replacement with OldString/NewString, or write entire content with Content. " +
    "For targeted edits, matches literal text (not regex) and fails if OldString is not found or is ambiguous. " +
    "For full writes, creates the file and parent directories if needed. " +
    "A successful result confirms the change; do not verify it with shell unless requested.",
    Grant = "file")]
public sealed partial class FileEditTool : NetclawTool<FileEditTool.Params>
{
    public const string ToolName = "file_edit";

    private readonly PathAccessPolicy _pathAccessPolicy;

    public record Params(
        [property: Description("File path to edit. Relative paths use the current project, then session_dir.")] string Path,
        [property: Description("The exact text to find in the file (omit when using Content for a full write)")] string? OldString = null,
        [property: Description("The text to replace OldString with (must differ from OldString; use empty string to delete)")] string? NewString = null,
        [property: Description("Replace all occurrences instead of just the first (default: false)")] bool? ReplaceAll = null,
        [property: Description("Full content to write to the file, creating parent directories if needed. Mutually exclusive with OldString/NewString.")] string? Content = null);

    public FileEditTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
        : this(new PathAccessPolicy(config, paths, pathPolicy))
    {
    }

    internal FileEditTool(PathAccessPolicy pathAccessPolicy)
    {
        _pathAccessPolicy = pathAccessPolicy;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return context.InvalidInput("Error: 'Path' parameter is required.");

        // Full-write mode: Content is supplied without OldString
        if (args.Content is not null)
        {
            if (args.OldString is not null || args.NewString is not null || args.ReplaceAll is not null)
                return context.InvalidInput(
                    "Error: 'Content' and 'OldString'/'NewString'/'ReplaceAll' are mutually exclusive. " +
                    "Use Content for a full file write, or OldString/NewString for targeted replacement.");

            return await WriteFileAsync(args.Path, args.Content, context, ct);
        }

        // Targeted edit mode: OldString → NewString
        if (string.IsNullOrEmpty(args.OldString))
            return context.InvalidInput("Error: either 'OldString' (for targeted edit) or 'Content' (for full write) is required.");

        if (args.NewString is null)
            return context.InvalidInput("Error: 'NewString' is required when using OldString for targeted replacement.");

        if (args.NewString == args.OldString)
            return context.InvalidInput("Error: 'NewString' must be different from 'OldString'.");

        return await EditFileAsync(args.Path, args.OldString, args.NewString, args.ReplaceAll == true, context, ct);
    }

    internal async Task<string> WriteFileAsync(string path, string content, ToolInvocationContext context, CancellationToken ct)
    {
        var access = _pathAccessPolicy.Evaluate(path, context, PathAccessPolicy.FileOperation.Write);
        if (!access.Allowed)
            return context.PathAccessFailure(access.Error, access.Failure ?? PathAccessPolicy.PathAccessFailure.AccessDenied);

        var authorizedPath = access.CanonicalPath;

        try
        {
            var directory = System.IO.Path.GetDirectoryName(authorizedPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var bytes = Encoding.UTF8.GetBytes(content);

            return await FileMutationGate.RunExclusiveAsync(authorizedPath, async () =>
            {
                await File.WriteAllBytesAsync(authorizedPath, bytes, ct);
                return context.SuccessFile(
                    $"Successfully wrote {bytes.Length} bytes to {authorizedPath}",
                    authorizedPath,
                    ToolFileActivityKind.Changed);
            }, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return context.AccessDenied($"Error: Permission denied: {authorizedPath}");
        }
        catch (DirectoryNotFoundException)
        {
            return context.NotFound($"Error: Directory not found: {authorizedPath}");
        }
        catch (IOException ex)
        {
            return context.TransientFailure($"Error writing file: {ex.Message}");
        }
    }

    private async Task<string> EditFileAsync(string path, string oldString, string newString, bool replaceAll, ToolInvocationContext context, CancellationToken ct)
    {
        var access = _pathAccessPolicy.Evaluate(path, context, PathAccessPolicy.FileOperation.Write);
        if (!access.Allowed)
            return context.PathAccessFailure(access.Error, access.Failure ?? PathAccessPolicy.PathAccessFailure.AccessDenied);

        var authorizedPath = access.CanonicalPath;

        if (!File.Exists(authorizedPath))
            return context.NotFound($"Error: File not found: {authorizedPath}");

        try
        {
            // Serialize the read-modify-write so concurrent same-file edits in
            // one turn apply sequentially instead of clobbering each other.
            return await FileMutationGate.RunExclusiveAsync(authorizedPath, async () =>
            {
                var content = await File.ReadAllTextAsync(authorizedPath, Encoding.UTF8, ct);

                var occurrences = CountOccurrences(content, oldString);

                if (occurrences == 0)
                    return context.RecoverableCorrection(
                        $"Error: The specified text was not found in {authorizedPath}",
                        ToolRemediationCode.ProvideUniqueOldString);

                if (occurrences > 1 && !replaceAll)
                    return context.RecoverableCorrection(
                        $"Error: OldString matches {occurrences} locations in {authorizedPath}.",
                        ToolRemediationCode.ProvideUniqueOldString);

                string newContent;
                int replacementCount;

                if (replaceAll)
                {
                    newContent = content.Replace(oldString, newString, StringComparison.Ordinal);
                    replacementCount = occurrences;
                }
                else
                {
                    var index = content.IndexOf(oldString, StringComparison.Ordinal);
                    newContent = string.Concat(
                        content.AsSpan(0, index),
                        newString,
                        content.AsSpan(index + oldString.Length));
                    replacementCount = 1;
                }

                var bytes = Encoding.UTF8.GetBytes(newContent);
                await File.WriteAllBytesAsync(authorizedPath, bytes, ct);

                return context.SuccessFile(
                    $"Successfully edited {authorizedPath}: replaced {replacementCount} occurrence(s)",
                    authorizedPath,
                    ToolFileActivityKind.Changed);
            }, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return context.AccessDenied($"Error: Permission denied: {authorizedPath}");
        }
        catch (FileNotFoundException)
        {
            return context.NotFound($"Error: File not found: {authorizedPath}");
        }
        catch (DirectoryNotFoundException)
        {
            return context.NotFound($"Error: File not found: {authorizedPath}");
        }
        catch (IOException ex)
        {
            return context.TransientFailure($"Error editing file: {ex.Message}");
        }
    }

    private static int CountOccurrences(string content, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
