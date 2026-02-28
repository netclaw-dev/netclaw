using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Writes a file to the agent's identity directory (~/.netclaw/identity/).
/// Path-restricted: only files under the identity directory are writable.
/// Creates parent directories automatically. Atomic write via temp + rename.
/// </summary>
[NetclawTool("identity_write",
    "Write a file to the agent identity directory. Use relative paths like 'SOUL.md' or 'soul/traits.md'. "
    + "Creates parent directories if needed.",
    Grant = "identity")]
public sealed partial class IdentityWriteTool : NetclawTool<IdentityWriteTool.Params>
{
    private readonly string _identityRoot;

    /// <summary>Maximum file size in bytes (50 KB).</summary>
    internal const int MaxFileSizeBytes = 50 * 1024;

    public record Params(
        [property: Description("Relative path within the identity directory (e.g. 'SOUL.md', 'soul/traits.md')")] string Path,
        [property: Description("Content to write to the file")] string Content);

    public IdentityWriteTool(NetclawPaths paths)
    {
        _identityRoot = paths.IdentityDirectory;
    }

    internal IdentityWriteTool(string identityRoot)
    {
        _identityRoot = identityRoot;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'path' parameter is required.";

        var resolved = ResolveSafePath(args.Path);
        if (resolved is null)
            return "Error: Path must be within the identity directory. Path traversal is not allowed.";

        var bytes = Encoding.UTF8.GetBytes(args.Content ?? string.Empty);
        if (bytes.Length > MaxFileSizeBytes)
            return $"Error: Content exceeds maximum file size of {MaxFileSizeBytes / 1024} KB.";

        try
        {
            var directory = System.IO.Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Atomic write: write to temp file then rename
            var tempPath = resolved + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            File.Move(tempPath, resolved, overwrite: true);

            return $"Successfully wrote {bytes.Length} bytes to identity/{args.Path}";
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    private string? ResolveSafePath(string relativePath)
    {
        if (System.IO.Path.IsPathRooted(relativePath))
            return null;

        var combined = System.IO.Path.Combine(_identityRoot, relativePath);
        var canonical = System.IO.Path.GetFullPath(combined);

        var rootCanonical = System.IO.Path.GetFullPath(_identityRoot);
        if (!rootCanonical.EndsWith(System.IO.Path.DirectorySeparatorChar))
            rootCanonical += System.IO.Path.DirectorySeparatorChar;

        return canonical.StartsWith(rootCanonical, StringComparison.Ordinal) ? canonical : null;
    }
}
