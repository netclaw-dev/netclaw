// -----------------------------------------------------------------------
// <copyright file="ShellTrustZonePolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Implements <see cref="IShellTrustZonePolicy"/> by delegating to
/// <see cref="ScopedFileAccessPolicy"/>. Shell commands are treated as having
/// write-equivalent privilege, so a path is authorized exactly when
/// <c>file_write</c> would authorize it — unifying the two surfaces on one
/// interpretation of the audience's write filesystem mode.
/// </summary>
public sealed class ShellTrustZonePolicy : IShellTrustZonePolicy
{
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public ShellTrustZonePolicy(ToolConfig toolConfig, NetclawPaths? paths = null)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(toolConfig, paths);
    }

    internal ShellTrustZonePolicy(ScopedFileAccessPolicy fileAccessPolicy)
    {
        _fileAccessPolicy = fileAccessPolicy;
    }

    public bool IsShellWritePathAuthorized(string fullPath, ToolExecutionContext context)
        => _fileAccessPolicy.TryResolveWritePath(fullPath, context, out _, out _);
}
