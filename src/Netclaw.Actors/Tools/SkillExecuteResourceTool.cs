// -----------------------------------------------------------------------
// <copyright file="SkillExecuteResourceTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Executes a resource file from a registered skill after staging it outside
/// sync-managed skill directories.
/// </summary>
[NetclawTool(ToolName,
    "Execute a resource file bundled with a registered skill. The resource is validated under the skill root, staged into the session directory, and run with shell-equivalent approval. Use for executable skill helpers such as Python or Bash scripts.",
    Grant = "shell")]
public sealed partial class SkillExecuteResourceTool : NetclawTool<SkillExecuteResourceTool.Params>
{
    public const string ToolName = "skill_execute_resource";

    private static readonly HashSet<string> AllowedInterpreters = new(StringComparer.Ordinal)
    {
        "bash",
        "sh",
        "python3",
        "python",
        "node",
        "pwsh",
        "powershell"
    };

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly SkillRegistry _skillRegistry;
    private readonly ISkillContentScanner _scanner;
    private readonly SkillSyncConfig _skillSyncConfig;
    private readonly ShellTool _shellTool;

    public record Params(
        [property: Description("Name of the skill containing the executable resource")]
        string SkillName,
        [property: Description("Relative path within the skill directory, for example 'scripts/check.py' or 'examples/demo.sh'")]
        string ResourcePath,
        [property: Description("Optional interpreter override: bash, sh, python3, python, node, pwsh, or powershell. Omit to infer from extension or shebang.")]
        string? Interpreter = null,
        [property: Description("Optional raw command-line arguments appended after the staged resource path")]
        string? Arguments = null);

    public SkillExecuteResourceTool(
        SkillRegistry skillRegistry,
        ISkillContentScanner scanner,
        ToolConfig toolConfig,
        ToolPathPolicy pathPolicy,
        ShellCommandPolicy commandPolicy,
        SkillSyncConfig skillSyncConfig)
    {
        ArgumentNullException.ThrowIfNull(skillSyncConfig);

        _skillRegistry = skillRegistry;
        _scanner = scanner;
        _skillSyncConfig = skillSyncConfig;
        _shellTool = new ShellTool(toolConfig, pathPolicy, commandPolicy);
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => await ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var audience = context.Audience;
        if (audience != TrustAudience.Personal || !_skillSyncConfig.Enabled)
            return "Error: This tool is not available.";

        if (string.IsNullOrWhiteSpace(context.SessionDirectory))
            return "Error: skill resource execution requires a session directory for staging.";

        var skillName = args.SkillName.Trim().ToLowerInvariant();
        var skill = _skillRegistry.GetAll()
            .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
            return $"Skill '{skillName}' not found.";

        var resourcePath = NormalizeResourcePath(args.ResourcePath);
        if (resourcePath is null)
            return "Resource path must be relative and must not contain path traversal.";

        var resolved = ResolveResourcePath(skill, resourcePath);
        if (resolved.Error is not null)
            return resolved.Error;

        string content;
        try
        {
            content = await File.ReadAllTextAsync(resolved.FullPath!, StrictUtf8, ct);
        }
        catch (DecoderFallbackException)
        {
            return "Error: skill resource execution supports text scripts only.";
        }
        catch (IOException ex)
        {
            return $"Failed to read resource: {ex.Message}";
        }

        var scanResult = await _scanner.ScanAsync($"{skillName}:{resourcePath}", content, ct);
        if (!scanResult.IsAllowed)
            return $"Resource '{resourcePath}' blocked by content scan: {scanResult.Reason}";

        var interpreter = ResolveInterpreter(args.Interpreter, resourcePath, content);
        if (interpreter.Error is not null)
            return interpreter.Error;

        string stagedPath;
        try
        {
            stagedPath = await StageResourceAsync(context.SessionDirectory, resourcePath, content, ct);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return $"Error staging resource: {ex.Message}";
        }

        var command = BuildShellCommand(interpreter.Value!, stagedPath, args.Arguments);
        var output = await _shellTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = context.ResolveShellCwd(null)
            },
            context,
            ct);

        if (scanResult.Verdict == ScanVerdict.Warning)
            return $":warning: Resource '{resourcePath}' triggered a content scan warning: {scanResult.Reason}\n\n{output}";

        return output;
    }

    private static string? NormalizeResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal))
            return null;

        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0
                                           || string.Equals(segment, ".", StringComparison.Ordinal)
                                           || string.Equals(segment, "..", StringComparison.Ordinal)))
            return null;

        return string.Join('/', segments);
    }

    private static ResourceResolution ResolveResourcePath(SkillEntry skill, string resourcePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(skill.SkillDirectory, resourcePath));
        var skillDirFull = Path.GetFullPath(skill.SkillDirectory);

        if (!PathUtility.IsWithinRoot(fullPath, skillDirFull))
            return new ResourceResolution(null, "Resolved path is outside the skill directory.");

        if (ContainsSymlink(fullPath, skillDirFull))
            return new ResourceResolution(null, "Symlink traversal is not allowed in resource paths.");

        if (!File.Exists(fullPath))
            return new ResourceResolution(null, $"Resource '{resourcePath}' not found in skill '{skill.Name}'.");

        return new ResourceResolution(fullPath, null);
    }

    private static InterpreterResolution ResolveInterpreter(string? requestedInterpreter, string resourcePath, string content)
    {
        if (!string.IsNullOrWhiteSpace(requestedInterpreter))
        {
            var normalized = requestedInterpreter.Trim();
            return AllowedInterpreters.Contains(normalized)
                ? new InterpreterResolution(normalized, null)
                : new InterpreterResolution(null,
                    $"Error: Interpreter '{requestedInterpreter}' is not supported. Supported interpreters: {string.Join(", ", AllowedInterpreters)}.");
        }

        var extension = Path.GetExtension(resourcePath).ToLowerInvariant();
        var inferred = extension switch
        {
            ".sh" or ".bash" => "bash",
            ".py" => "python3",
            ".js" or ".mjs" or ".cjs" => "node",
            ".ps1" => "pwsh",
            _ => InferInterpreterFromShebang(content)
        };

        return inferred is not null
            ? new InterpreterResolution(inferred, null)
            : new InterpreterResolution(null,
                "Error: Unable to infer interpreter. Provide Interpreter as one of: "
                + string.Join(", ", AllowedInterpreters) + ".");
    }

    private static string? InferInterpreterFromShebang(string content)
    {
        if (!content.StartsWith("#!", StringComparison.Ordinal))
            return null;

        var firstLineEnd = content.IndexOf('\n', StringComparison.Ordinal);
        var shebang = (firstLineEnd >= 0 ? content[..firstLineEnd] : content).ToLowerInvariant();

        if (shebang.Contains("python3", StringComparison.Ordinal))
            return "python3";
        if (shebang.Contains("python", StringComparison.Ordinal))
            return "python3";
        if (shebang.Contains("bash", StringComparison.Ordinal))
            return "bash";
        if (shebang.Contains("node", StringComparison.Ordinal))
            return "node";
        if (shebang.Contains("pwsh", StringComparison.Ordinal))
            return "pwsh";
        if (shebang.Contains("sh", StringComparison.Ordinal))
            return "sh";

        return null;
    }

    private static async Task<string> StageResourceAsync(
        string sessionDirectory,
        string resourcePath,
        string content,
        CancellationToken ct)
    {
        var sessionRoot = Path.GetFullPath(sessionDirectory);
        var stagingRoot = Path.Combine(sessionRoot, "skill-resources");
        if (ContainsSymlink(stagingRoot, sessionRoot))
            throw new IOException("Session skill resource staging directory is a symlink.");

        Directory.CreateDirectory(stagingRoot);
        if (ContainsSymlink(stagingRoot, sessionRoot))
            throw new IOException("Session skill resource staging directory is a symlink.");

        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        var fileName = Path.GetFileName(resourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Resource path does not identify a file.", nameof(resourcePath));

        var stagingPath = Path.GetFullPath(Path.Combine(stagingDirectory, fileName));
        if (!PathUtility.IsWithinRoot(stagingPath, sessionRoot))
            throw new IOException("Resolved staging path is outside the session directory.");

        await using var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, StrictUtf8);
        await writer.WriteAsync(content.AsMemory(), ct);
        return stagingPath;
    }

    private static string BuildShellCommand(string interpreter, string stagedPath, string? arguments)
    {
        var renderedPath = RenderPathForInterpreter(interpreter, stagedPath);
        var command = string.Concat(interpreter, " ", QuoteShellToken(renderedPath));
        if (!string.IsNullOrWhiteSpace(arguments))
            command = string.Concat(command, " ", arguments.Trim());
        return command;
    }

    private static string RenderPathForInterpreter(string interpreter, string stagedPath)
    {
        if (!OperatingSystem.IsWindows() || !IsPosixShell(interpreter))
            return stagedPath;

        var fullPath = Path.GetFullPath(stagedPath);
        if (fullPath.Length >= 2 && fullPath[1] == ':')
        {
            var drive = char.ToLowerInvariant(fullPath[0]);
            var remainder = fullPath[2..].Replace('\\', '/');
            if (!remainder.StartsWith("/", StringComparison.Ordinal))
                remainder = string.Concat("/", remainder);

            return string.Concat("/", drive, remainder);
        }

        return fullPath.Replace('\\', '/');
    }

    private static bool IsPosixShell(string interpreter)
        => interpreter.Equals("bash", StringComparison.Ordinal)
           || interpreter.Equals("sh", StringComparison.Ordinal);

    private static string QuoteShellToken(string value)
    {
        if (OperatingSystem.IsWindows())
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool ContainsSymlink(string path, string root)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(root);
        var current = path;
        while (!string.IsNullOrEmpty(current)
            && !string.Equals(
                Path.TrimEndingDirectorySeparator(current), rootFull, StringComparison.Ordinal))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (new FileInfo(current).LinkTarget is not null)
                    return true;

                if (new DirectoryInfo(current).LinkTarget is not null)
                    return true;
            }

            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private sealed record ResourceResolution(string? FullPath, string? Error);
    private sealed record InterpreterResolution(string? Value, string? Error);
}
