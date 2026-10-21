// -----------------------------------------------------------------------
// <copyright file="PosixUserId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.InteropServices;

namespace Netclaw.Tools;

/// <summary>Reads the effective user ID of the process on a POSIX host.</summary>
/// <remarks>
/// .NET has no public API for the user ID. The private managed temporary base
/// <c>/tmp/netclaw-&lt;uid&gt;</c> and its owner check both need it.
/// </remarks>
internal static partial class PosixUserId
{
    // geteuid cannot fail (POSIX), so the call needs no SetLastError and has no error return.
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint NativeGetEffectiveUserId();

    /// <summary>Gets the effective user ID of this process.</summary>
    /// <exception cref="PlatformNotSupportedException">The host is Windows.</exception>
    internal static uint GetEffective()
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The effective user ID exists only on POSIX hosts.");

        return NativeGetEffectiveUserId();
    }
}
