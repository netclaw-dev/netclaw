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

    public ShellTrustZonePolicy(ToolConfig toolConfig, NetclawPaths paths)
    {
        _fileAccessPolicy = new ScopedFileAccessPolicy(toolConfig, paths);
    }

    internal ShellTrustZonePolicy(ScopedFileAccessPolicy fileAccessPolicy)
    {
        _fileAccessPolicy = fileAccessPolicy;
    }

    // Shell paths are checked against the stricter WRITE rules even for read-only
    // commands: a shell command can write, so we treat every path token as
    // write-level. Deliberately over-cautious — e.g. a non-interactive `cat` of a
    // skills file is denied here even though the read-only file_read tool would
    // allow it (the read zone includes the global read roots, the write zone does
    // not). This is a known capability gap, not a security hole; making the check
    // verb-aware (read-verbs against the read zone) is a possible follow-up.
    public bool IsShellWritePathAuthorized(string fullPath, ToolInvocationContext context)
        => _fileAccessPolicy.TryResolveWritePath(fullPath, context, out _, out _);
}
