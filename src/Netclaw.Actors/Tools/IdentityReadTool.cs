using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Reads a file from the agent's identity directory (~/.netclaw/identity/).
/// Path-restricted: only files under the identity directory are accessible.
/// </summary>
[NetclawTool("identity_read",
    "Read a file from the agent identity directory. Use relative paths like 'SOUL.md' or 'soul/traits.md'.",
    Grant = "identity")]
public sealed partial class IdentityReadTool : NetclawTool<IdentityReadTool.Params>
{
    private readonly string _identityRoot;

    public record Params(
        [property: Description("Relative path within the identity directory (e.g. 'SOUL.md', 'soul/traits.md')")] string Path);

    public IdentityReadTool(NetclawPaths paths)
    {
        _identityRoot = paths.IdentityDirectory;
    }

    internal IdentityReadTool(string identityRoot)
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

        if (!File.Exists(resolved))
            return $"Error: File not found: {args.Path}";

        try
        {
            var content = await File.ReadAllTextAsync(resolved, Encoding.UTF8, ct);
            return content;
        }
        catch (IOException ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    private string? ResolveSafePath(string relativePath)
    {
        // Reject absolute paths
        if (System.IO.Path.IsPathRooted(relativePath))
            return null;

        var combined = System.IO.Path.Combine(_identityRoot, relativePath);
        var canonical = System.IO.Path.GetFullPath(combined);

        // Ensure the resolved path is under the identity root
        var rootCanonical = System.IO.Path.GetFullPath(_identityRoot);
        if (!rootCanonical.EndsWith(System.IO.Path.DirectorySeparatorChar))
            rootCanonical += System.IO.Path.DirectorySeparatorChar;

        return canonical.StartsWith(rootCanonical, StringComparison.Ordinal) ? canonical : null;
    }
}
