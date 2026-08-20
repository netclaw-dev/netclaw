// -----------------------------------------------------------------------
// <copyright file="SetWorkingDirectoryTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Sets the session's project directory — the root of the codebase or project
/// the agent is currently working on. The owning session or subagent actor
/// intercepts successful results by tool name to update its project scope and
/// re-assemble the system prompt with project-scoped identity files.
/// </summary>
[NetclawTool(ToolName,
    "Call this once before tool work in a named project. Do not call it again when the current project already matches. " +
    "Declare the named path before probing it. If rejected, retry the user-provided fallback before other tool work. " +
    "Use the task's first project path exactly; do not substitute its parent before this tool rejects it. " +
    "It declares the project root and expands your trusted scope. " +
    "Once set, read-only phrases (ls, grep, cat, git status, git ls-tree, ...) inside that tree " +
    "auto-run without prompting — the safe-verb short-circuit treats the directory as a safe space. " +
    "Mutating commands still prompt, but the prompt shows the right cwd so persisted approvals are " +
    "correctly scoped. Also loads the project's identity file (AGENTS.md / CLAUDE.md / etc.) into the " +
    "system prompt. Note: shell commands that pass a path argument (e.g. `find /repo`, `ls /var/log`) " +
    "provide exact approval scope but do not declare the project root. " +
    "Use an absolute path to the project root.",
    Grant = "file")]
public sealed partial class SetWorkingDirectoryTool : NetclawTool<SetWorkingDirectoryTool.Params>
{
    public const string ToolName = "set_working_directory";

    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [param: Description("Absolute path to the project root for the current task.")]
        string Path);

    public SetWorkingDirectoryTool(ToolConfig config, NetclawPaths paths)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (ContainsInvalidControlCharacter(args.Path))
            return Task.FromResult("Error: path contains an invalid control character.");

        var raw = args.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
            return Task.FromResult("Error: path is required.");

        if (!_fileAccessPolicy.TryResolveWorkingDirectory(raw, context, out var fullPath, out var accessError))
            return Task.FromResult(accessError);

        if (!Directory.Exists(fullPath))
            return Task.FromResult($"Error: directory does not exist: {fullPath}");

        return Task.FromResult(fullPath);
    }

    internal bool CanDeclare(string path, ToolInvocationContext context)
        => !ContainsInvalidControlCharacter(path)
           && _fileAccessPolicy.TryResolveWorkingDirectory(
               path,
               context,
               out var fullPath,
               out _)
           && PathUtility.AreEquivalentPaths(path, fullPath)
           && Directory.Exists(fullPath);

    private static bool ContainsInvalidControlCharacter(string? path)
        => path is not null && path.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0;

}
