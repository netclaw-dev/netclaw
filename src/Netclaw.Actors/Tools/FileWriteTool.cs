using System.ComponentModel;
using System.Text;
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
    public record Params(
        [property: Description("Absolute path to the file to write")] string Path,
        [property: Description("Content to write to the file")] string Content);

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'path' parameter is required.";

        try
        {
            var directory = System.IO.Path.GetDirectoryName(args.Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var bytes = Encoding.UTF8.GetBytes(args.Content);
            await File.WriteAllBytesAsync(args.Path, bytes, ct);

            return $"Successfully wrote {bytes.Length} bytes to {args.Path}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: Permission denied: {args.Path}";
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }
}
