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
/// <see cref="ScopedFileAccessPolicy"/> for root resolution. Returns the
/// write-access roots for the context — shell commands are treated as having
/// write-equivalent privilege since they can modify files.
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

    public IReadOnlyList<string> GetTrustZoneRoots(ToolExecutionContext context)
        => _fileAccessPolicy.GetRootsForContext(context, ScopedFileAccessPolicy.AccessKind.Write);
}
