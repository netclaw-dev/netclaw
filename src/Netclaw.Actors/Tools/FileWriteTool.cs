// -----------------------------------------------------------------------
// <copyright file="FileWriteTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Backward-compatible alias for <see cref="FileEditTool"/>'s full-write mode.
/// Delegates to <see cref="FileEditTool.WriteFileAsync"/> so the write logic
/// lives in one place. Registered under <c>file_write</c> so existing sessions,
/// approval grants, and audience profiles continue to work.
/// </summary>
[NetclawTool(ToolName,
    "Use for creating or replacing a known local file without shell. Write content and create parent directories when needed.",
    Grant = "file")]
public sealed partial class FileWriteTool : NetclawTool<FileWriteTool.Params>
{
    public const string ToolName = "file_write";

    private readonly FileEditTool _editTool;

    public record Params(
        [property: Description("Absolute path to the file to write")] string Path,
        [property: Description("Content to write to the file")] string Content);

    public FileWriteTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
    {
        _editTool = new FileEditTool(config, paths, pathPolicy);
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return Task.FromResult("Error: 'Path' parameter is required.");

        return _editTool.WriteFileAsync(args.Path, args.Content, context, ct);
    }
}
