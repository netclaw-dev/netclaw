// -----------------------------------------------------------------------
// <copyright file="SetWorkingDirectoryTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Sets the session's project directory — the root of the codebase or project
/// the agent is currently working on. The session actor intercepts successful
/// results by tool name to update <c>WorkingContext.ProjectDirectory</c> and
/// re-assemble the system prompt with project-scoped identity files.
/// </summary>
[NetclawTool(ToolName,
    "Declare your project root and expand your trusted scope. " +
    "Once set, read-only verbs (ls, grep, cat, git status, git log, ...) inside that tree " +
    "auto-run without prompting — the safe-verb short-circuit treats the directory as a safe space. " +
    "Mutating commands still prompt, but the prompt shows the right cwd so persisted approvals are " +
    "correctly scoped. Also loads the project's identity file (AGENTS.md / CLAUDE.md / etc.) into the " +
    "system prompt. Note: shell commands that pass a path argument (e.g. `find /repo`, `ls /var/log`) " +
    "declare scope implicitly via that argument, so this tool is most useful for sessions where you'll " +
    "run multiple commands without explicit paths (git status, git diff, make build, etc.). " +
    "Use an absolute path to the project root.",
    Grant = "file")]
public sealed partial class SetWorkingDirectoryTool : NetclawTool<SetWorkingDirectoryTool.Params>
{
    public const string ToolName = "set_working_directory";

    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("Absolute path to the project root directory.")]
        string Path);

    public SetWorkingDirectoryTool(ToolConfig config, NetclawPaths paths)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        var raw = args.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
            return Task.FromResult("Error: path is required.");

        if (!_fileAccessPolicy.TryResolveWorkingDirectory(raw, context, out var fullPath, out var accessError))
            return Task.FromResult(accessError);

        if (!Directory.Exists(fullPath))
            return Task.FromResult($"Error: directory does not exist: {fullPath}");

        return Task.FromResult(fullPath);
    }

}
