// -----------------------------------------------------------------------
// <copyright file="ShellRedirectPolicyFacts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

internal static class ShellRedirectPolicyFacts
{
    internal static bool HasFileWritingRedirect(CommandOccurrence occurrence) =>
        occurrence.Redirects.Any(static redirect => redirect is FileRedirectAnalysis
        {
            Mode: var mode
        } && IsFileWritingMode(mode));

    internal static bool IsFileWritingMode(FileRedirectMode mode)
        => mode is FileRedirectMode.Output
            or FileRedirectMode.Append
            or FileRedirectMode.CombinedOutput
            or FileRedirectMode.CombinedOutputAppend;

    /// <summary>
    /// True when a resolved redirect target is the POSIX null device. Output
    /// sent there is discarded: the redirect writes no file.
    /// </summary>
    /// <remarks>
    /// Only the exact resolved POSIX path <c>/dev/null</c> qualifies. Other
    /// device paths and any unresolved target stay file writes. Windows
    /// <c>NUL</c> is not a sink here: PowerShell resolves it to a full path
    /// before the Win32 call, and the device mapping of that form is not proved.
    /// </remarks>
    internal static bool IsNullDevice(CanonicalPath path)
        => path.Style == ShellPathStyle.Posix
           && string.Equals(path.Value, "/dev/null", StringComparison.Ordinal);
}
