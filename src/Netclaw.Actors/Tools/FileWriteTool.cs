using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Writes content to a file as UTF-8, creating parent directories if needed.
/// </summary>
[NetclawTool("file_write",
    "Write content to a file, creating parent directories if needed",
    Grant = "file")]
public sealed partial class FileWriteTool : NetclawTool<FileWriteTool.Params>
{
    private readonly ToolPathPolicy? _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("Absolute path to the file to write")] string Path,
        [property: Description("Content to write to the file")] string Content);

    public FileWriteTool(ToolPathPolicy? pathPolicy = null)
    {
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(new ToolConfig());
    }

    public FileWriteTool(ToolConfig config, ToolPathPolicy? pathPolicy = null)
    {
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(config);
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'path' parameter is required.";

        if (!_fileAccessPolicy.TryResolveWritePath(args.Path, context, out var authorizedPath, out var accessError))
            return accessError;

        if (_pathPolicy?.IsDenied(authorizedPath) == true)
            return "Error: Access denied — this file is protected by security policy.";

        try
        {
            var directory = System.IO.Path.GetDirectoryName(authorizedPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var bytes = Encoding.UTF8.GetBytes(args.Content);
            await File.WriteAllBytesAsync(authorizedPath, bytes, ct);

            return $"Successfully wrote {bytes.Length} bytes to {authorizedPath}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: Permission denied: {authorizedPath}";
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }
}
