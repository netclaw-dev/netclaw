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
    "It declares the project root for path authorization and shell approval. " +
    "Once set, read-only phrases (ls, grep, cat, git status, git ls-tree, ...) inside that tree " +
    "can auto-run without prompting when reviewed-safe policy allows them. " +
    "Mutating commands still prompt, but the prompt shows the right cwd so persisted approvals are " +
    "correctly scoped. Also loads the project's identity file (AGENTS.md / CLAUDE.md / etc.) into the " +
    "system prompt. Note: shell commands that pass a path argument (e.g. `find /repo`, `ls /var/log`) " +
    "provide exact approval scope but do not declare the project root. " +
    "Use an absolute path to the project root.",
    Grant = "file")]
public sealed partial class SetWorkingDirectoryTool : NetclawTool<SetWorkingDirectoryTool.Params>
{
    public const string ToolName = "set_working_directory";

    private readonly PathAccessPolicy _pathAccessPolicy;

    public record Params(
        [param: Description("Absolute path to the project root for the current task.")]
        string Path);

    public SetWorkingDirectoryTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
        : this(new PathAccessPolicy(config, paths, pathPolicy))
    {
    }

    internal SetWorkingDirectoryTool(PathAccessPolicy pathAccessPolicy)
    {
        _pathAccessPolicy = pathAccessPolicy;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (ContainsInvalidControlCharacter(args.Path))
            return Task.FromResult(context.InvalidInput("Error: path contains an invalid control character."));

        var raw = args.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
            return Task.FromResult(context.InvalidInput("Error: path is required."));

        if (!Path.IsPathFullyQualified(raw))
            return Task.FromResult(context.InvalidInput("Error: path must be absolute."));

        var access = _pathAccessPolicy.Evaluate(raw, context, PathAccessPolicy.FileOperation.DeclareProjectScope);
        if (!access.Allowed)
            return Task.FromResult(context.PathAccessFailure(access.Error, access.Failure ?? PathAccessPolicy.PathAccessFailure.AccessDenied));

        var fullPath = access.CanonicalPath;

        if (!Directory.Exists(fullPath))
            return Task.FromResult(context.NotFound($"Error: directory does not exist: {fullPath}"));

        return Task.FromResult(context.SuccessProject(fullPath, fullPath));
    }

    internal bool CanDeclare(string path, ToolInvocationContext context)
        => !ContainsInvalidControlCharacter(path)
           && _pathAccessPolicy.Evaluate(
               path,
               context,
               PathAccessPolicy.FileOperation.DeclareProjectScope) is { Allowed: true } access
           && PathUtility.AreEquivalentPaths(path, access.CanonicalPath)
           && Directory.Exists(access.CanonicalPath);

    private static bool ContainsInvalidControlCharacter(string? path)
        => path is not null && path.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0;

}
