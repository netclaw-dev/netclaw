using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Lists files and directories under the agent's identity directory (~/.netclaw/identity/).
/// Path-restricted: only contents under the identity directory are listed.
/// </summary>
[NetclawTool("identity_list",
    "List files and directories in the agent identity directory. "
    + "Optionally specify a subdirectory like 'soul/' to list its contents.",
    Grant = "identity")]
public sealed partial class IdentityListTool : NetclawTool<IdentityListTool.Params>
{
    private readonly string _identityRoot;

    public record Params(
        [property: Description("Relative subdirectory to list (optional, defaults to root). E.g. 'soul/', 'agents/'")] string? Path = null);

    public IdentityListTool(NetclawPaths paths)
    {
        _identityRoot = paths.IdentityDirectory;
    }

    internal IdentityListTool(string identityRoot)
    {
        _identityRoot = identityRoot;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var targetDir = _identityRoot;

        if (!string.IsNullOrWhiteSpace(args.Path))
        {
            var resolved = ResolveSafePath(args.Path);
            if (resolved is null)
                return Task.FromResult("Error: Path must be within the identity directory. Path traversal is not allowed.");
            targetDir = resolved;
        }

        if (!Directory.Exists(targetDir))
            return Task.FromResult($"Error: Directory not found: {args.Path ?? "/"}");

        try
        {
            var sb = new StringBuilder();
            var rootCanonical = Path.GetFullPath(_identityRoot);

            // List directories first, then files
            foreach (var dir in Directory.GetDirectories(targetDir))
            {
                var relative = Path.GetRelativePath(rootCanonical, dir);
                sb.AppendLine($"  {relative}/");
            }

            foreach (var file in Directory.GetFiles(targetDir))
            {
                var relative = Path.GetRelativePath(rootCanonical, file);
                var info = new FileInfo(file);
                sb.AppendLine($"  {relative} ({info.Length} bytes)");
            }

            return sb.Length > 0
                ? Task.FromResult(sb.ToString())
                : Task.FromResult("(empty directory)");
        }
        catch (IOException ex)
        {
            return Task.FromResult($"Error listing directory: {ex.Message}");
        }
    }

    private string? ResolveSafePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return null;

        var combined = Path.Combine(_identityRoot, relativePath);
        var canonical = Path.GetFullPath(combined);

        var rootCanonical = Path.GetFullPath(_identityRoot);
        if (!rootCanonical.EndsWith(Path.DirectorySeparatorChar))
            rootCanonical += Path.DirectorySeparatorChar;

        return canonical.StartsWith(rootCanonical, StringComparison.Ordinal) ? canonical : null;
    }
}
