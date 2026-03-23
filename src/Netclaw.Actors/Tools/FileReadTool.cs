using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Reads file contents as UTF-8 text with optional line offset/limit.
/// </summary>
[NetclawTool("file_read",
    "Read the contents of a file as text",
    Grant = "file")]
public sealed partial class FileReadTool : NetclawTool<FileReadTool.Params>
{
    private readonly ToolConfig _config;
    private readonly ToolPathPolicy? _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("Absolute path to the file to read")] string Path,
        [property: Description("Line number to start reading from (1-based, optional)")] int? Offset = null,
        [property: Description("Maximum number of lines to read (optional)")] int? Limit = null);

    public FileReadTool(ToolConfig config, ToolPathPolicy? pathPolicy = null)
    {
        _config = config;
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(config);
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'path' parameter is required.";

        if (!_fileAccessPolicy.TryResolveReadPath(args.Path, context, out var authorizedPath, out var accessError))
            return accessError;

        if (_pathPolicy?.IsDenied(authorizedPath) == true)
            return "Error: Access denied — this file is protected by security policy.";

        if (!File.Exists(authorizedPath))
            return $"Error: File not found: {authorizedPath}";

        // Treat 0 or negative as "not specified"
        int? offset = args.Offset > 0 ? args.Offset : null;
        int? limit = args.Limit > 0 ? args.Limit : null;

        try
        {
            if (offset.HasValue || limit.HasValue)
            {
                return await ReadLinesAsync(authorizedPath, offset ?? 1, limit, _config.MaxOutputChars, ct);
            }

            var content = await File.ReadAllTextAsync(authorizedPath, Encoding.UTF8, ct);
            return ShellTool.TruncateOutput(content, _config.MaxOutputChars);
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: Permission denied: {authorizedPath}";
        }
        catch (IOException ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    private static async Task<string> ReadLinesAsync(
        string path, int startLine, int? maxLines, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var lineNumber = 0;
        var linesRead = 0;

        using var reader = new StreamReader(path, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (lineNumber < startLine)
                continue;

            if (maxLines.HasValue && linesRead >= maxLines.Value)
                break;

            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append($"{lineNumber,6}\t{line}");
            linesRead++;

            if (sb.Length >= maxChars)
            {
                return ShellTool.TruncateOutput(sb.ToString(), maxChars);
            }
        }

        return sb.ToString();
    }
}
