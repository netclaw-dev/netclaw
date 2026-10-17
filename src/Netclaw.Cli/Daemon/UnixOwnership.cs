// -----------------------------------------------------------------------
// <copyright file="UnixOwnership.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.InteropServices;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Reads the user id of a file owner on Linux. .NET has no public API for it. The call is
/// <c>statx(2)</c>, because its result layout is the same on every CPU architecture, unlike
/// <c>stat(2)</c>.
/// </summary>
internal static partial class UnixOwnership
{
    private const int CurrentDirectoryFd = -100;
    private const uint StatxUid = 0x8;

    internal static bool IsSupported => OperatingSystem.IsLinux();

    internal static uint CurrentUserId() => GetEffectiveUserId();

    internal static bool TryGetOwnerId(string path, out uint ownerId)
    {
        ownerId = 0;
        if (!IsSupported || StatX(CurrentDirectoryFd, path, flags: 0, StatxUid, out var result) != 0)
            return false;

        ownerId = result.OwnerId;
        return (result.Mask & StatxUid) != 0;
    }

    // Only the first fields are named. The kernel struct is 256 bytes, and the size must hold all of them.
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Statx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint OwnerId;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int StatX(int directoryFd, string path, int flags, uint mask, out Statx result);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
