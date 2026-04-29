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
[NetclawTool("set_working_directory",
    "Set the project directory for this session. This determines which project's identity files " +
    "(AGENTS.md, CLAUDE.md) are loaded into context. Use an absolute path to the project root.",
    Grant = "file")]
public sealed partial class SetWorkingDirectoryTool : NetclawTool<SetWorkingDirectoryTool.Params>
{
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("Absolute path to the project root directory.")]
        string Path);

    public SetWorkingDirectoryTool(ToolConfig config)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(config);
    }

    protected override Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var raw = args.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
            return Task.FromResult("Error: path is required.");

        if (!_fileAccessPolicy.TryResolveReadPath(raw, context, out var fullPath, out var accessError))
            return Task.FromResult(accessError);

        if (!Directory.Exists(fullPath))
            return Task.FromResult($"Error: directory does not exist: {fullPath}");

        return Task.FromResult(fullPath);
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);
}
