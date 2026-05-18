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
/// Makes targeted text replacements in an existing file without rewriting the entire file.
/// Matches literal text (not regex). Fails if OldString is not found or is ambiguous.
/// </summary>
[NetclawTool(ToolName,
    "Make targeted text replacements in an existing file without rewriting the entire file",
    Grant = "file")]
public sealed partial class FileEditTool : NetclawTool<FileEditTool.Params>
{
    public const string ToolName = "file_edit";

    private readonly ToolPathPolicy? _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("Absolute path to the file to edit")] string Path,
        [property: Description("The exact text to find in the file")] string OldString,
        [property: Description("The text to replace it with (must differ from OldString; use empty string to delete)")] string NewString,
        [property: Description("Replace all occurrences instead of just the first (default: false)")] bool? ReplaceAll = null);

    public FileEditTool(ToolPathPolicy? pathPolicy = null)
    {
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(new ToolConfig());
    }

    public FileEditTool(ToolConfig config, ToolPathPolicy? pathPolicy = null)
    {
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(config);
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'Path' parameter is required.";

        if (string.IsNullOrEmpty(args.OldString))
            return "Error: 'OldString' parameter is required and must not be empty.";

        if (args.NewString == args.OldString)
            return "Error: 'NewString' must be different from 'OldString'.";

        if (!_fileAccessPolicy.TryResolveWritePath(args.Path, context, out var authorizedPath, out var accessError))
            return accessError;

        if (_pathPolicy?.IsDenied(authorizedPath) == true)
            return FileToolErrors.ControlPlaneWriteDenied(authorizedPath);

        if (!File.Exists(authorizedPath))
            return $"Error: File not found: {authorizedPath}";

        try
        {
            // Serialize the read-modify-write so concurrent same-file edits in
            // one turn apply sequentially instead of clobbering each other.
            return await FileMutationGate.RunExclusiveAsync(authorizedPath, async () =>
            {
                var content = await File.ReadAllTextAsync(authorizedPath, Encoding.UTF8, ct);

                var replaceAll = args.ReplaceAll == true;
                var occurrences = CountOccurrences(content, args.OldString);

                if (occurrences == 0)
                    return $"Error: The specified text was not found in {authorizedPath}";

                if (occurrences > 1 && !replaceAll)
                    return $"Error: OldString matches {occurrences} locations in {authorizedPath}. " +
                           "Provide more surrounding context to create a unique match, or set ReplaceAll=true.";

                string newContent;
                int replacementCount;

                if (replaceAll)
                {
                    newContent = content.Replace(args.OldString, args.NewString, StringComparison.Ordinal);
                    replacementCount = occurrences;
                }
                else
                {
                    // Single replacement at first occurrence
                    var index = content.IndexOf(args.OldString, StringComparison.Ordinal);
                    newContent = string.Concat(
                        content.AsSpan(0, index),
                        args.NewString,
                        content.AsSpan(index + args.OldString.Length));
                    replacementCount = 1;
                }

                var bytes = Encoding.UTF8.GetBytes(newContent);
                await File.WriteAllBytesAsync(authorizedPath, bytes, ct);

                return $"Successfully edited {authorizedPath}: replaced {replacementCount} occurrence(s)";
            }, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: Permission denied: {authorizedPath}";
        }
        catch (IOException ex)
        {
            return $"Error editing file: {ex.Message}";
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
