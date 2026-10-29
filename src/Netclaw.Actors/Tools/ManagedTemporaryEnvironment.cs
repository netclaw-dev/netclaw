// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Runtime.InteropServices;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>Prepares a child process to use one session-owned temporary directory.</summary>
internal static class ManagedTemporaryEnvironment
{
    /// <summary>
    /// Validates and creates the directory, checks filesystem links before and after creation,
    /// and sets the standard temporary environment variables on the child process only.
    /// </summary>
    /// <returns><c>null</c> on success; otherwise, a stable error that prevents process launch.</returns>
    internal static string? Prepare(
        ProcessStartInfo startInfo,
        ManagedTemporaryLocation location)
        => Prepare(startInfo, location, HostManagedTemporaryFolderInspector.Instance);

    /// <summary>
    /// Prepares the directory with an explicit folder inspector, then sets the temporary
    /// variables on the child process.
    /// </summary>
    internal static string? Prepare(
        ProcessStartInfo startInfo,
        ManagedTemporaryLocation location,
        IManagedTemporaryFolderInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (EnsureFolders(location, inspector) is { } refusal)
            return refusal;

        // The shell parser resolves these variables from the same values (ShellExecutionEnvironment.CreateLaunchEnvironment).
        ShellExecutionEnvironment.ApplyTemporaryVariables(startInfo.Environment, location);
        return null;
    }

    /// <summary>
    /// Creates and checks the folders of one location. The shell launch and each file tool
    /// write below the private base use this one path, so the first writer cannot leave a
    /// folder with group or other access (issue #2554).
    /// </summary>
    /// <remarks>
    /// For a location with a <see cref="ManagedTemporaryLocation.PrivateBase"/>, the base and the
    /// key folder must each be a folder that is not a link, that the daemon user owns, and that
    /// has no group or other permission bits. A failed check refuses the operation.
    /// </remarks>
    /// <returns><c>null</c> on success; otherwise, a stable error that refuses the operation.</returns>
    internal static string? EnsureFolders(ManagedTemporaryLocation location)
        => EnsureFolders(location, HostManagedTemporaryFolderInspector.Instance);

    private static string? EnsureFolders(
        ManagedTemporaryLocation location,
        IManagedTemporaryFolderInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);

        try
        {
            // The private folders come first: an unsafe base or key folder refuses the operation
            // before anything else is created. There is no fallback to another directory.
            if (location.PrivateBase is { } privateBase
                && CheckPrivateFolders(privateBase, location, inspector) is { } refusal)
            {
                return refusal;
            }

            var normalizedTemporaryDirectory = ShellExecutionEnvironment.GetTemporaryDirectoryValue(location);
            if (!CanonicalPath.TryCreateHost(location.StorageRoot.Value, relativeBase: null, out var root)
                || !CanonicalPath.TryCreateHost(normalizedTemporaryDirectory, relativeBase: null, out var temporary))
            {
                return "Error: The managed temporary directory contains an unsafe filesystem link.";
            }

            // Check before and after creation: a link can appear between the two.
            PathBoundary[] storage = [new PathBoundary.Folder(root, LinkRule.IncludingRoot)];
            if (FileSystemAuthority.EvaluateMembership(temporary, storage) is not PathDecision.Allowed)
                return "Error: The managed temporary directory contains an unsafe filesystem link.";

            CreateRunFolder(normalizedTemporaryDirectory, isPrivate: location.PrivateBase is not null);
            if (!Directory.Exists(normalizedTemporaryDirectory))
                return $"Error: Managed temporary directory '{normalizedTemporaryDirectory}' was not created.";

            if (FileSystemAuthority.EvaluateMembership(temporary, storage) is not PathDecision.Allowed)
                return "Error: The managed temporary directory contains an unsafe filesystem link.";

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return $"Error preparing managed temporary directory: {ex.Message}";
        }
    }

    private const UnixFileMode PrivateFolderMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode GroupAndOtherBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    // A run folder below the private key folder gets mode 0700 too. A deep folder keeps the
    // default mode, as before.
    private static void CreateRunFolder(string folder, bool isPrivate)
    {
        if (!isPrivate || OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            return;
        }

        Directory.CreateDirectory(folder, PrivateFolderMode);
    }

    /// <summary>
    /// Creates the per-user base and the key folder with mode 0700 and checks each one.
    /// </summary>
    /// <remarks>
    /// SECURITY: /tmp is shared and sticky. Another local user can create /tmp/netclaw-&lt;uid&gt;
    /// first, or plant a link there. A folder that already exists is never changed (no chmod, no
    /// chown): a changed foreign folder would hide the problem. The checks are call-local and no
    /// sandbox against a concurrent change; the link check after creation in
    /// <see cref="EnsureFolders(ManagedTemporaryLocation, IManagedTemporaryFolderInspector)"/>
    /// stays. A native error from the inspector throws, and the caller reports it as a refusal.
    /// </remarks>
    /// <returns><c>null</c> when both folders pass; otherwise the error that refuses the launch.</returns>
    private static string? CheckPrivateFolders(
        ManagedTemporaryBase privateBase,
        ManagedTemporaryLocation location,
        IManagedTemporaryFolderInspector inspector)
    {
        if (OperatingSystem.IsWindows())
            return "Error: A private managed temporary folder is not supported on Windows.";

        // The base first, then the key folder below it.
        foreach (var folder in new[] { privateBase.Value, location.StorageRoot.Value })
        {
            if (CreateAndCheckFolder(folder, inspector) is { } refusal)
                return refusal;
        }

        return null;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string? CreateAndCheckFolder(string folder, IManagedTemporaryFolderInspector inspector)
    {
        Directory.CreateDirectory(folder, PrivateFolderMode);
        return CheckFolder(folder, inspector);
    }

    /// <summary>
    /// Runs the shell launch's checks on the per-user base alone (created with mode 0700, then not a
    /// link, owned by the daemon user, no group or other bit). The daemon calls it at startup.
    /// </summary>
    /// <returns><c>null</c> when the shell launch would accept the base; otherwise its reason.</returns>
    internal static ManagedTemporaryBaseRefusal? CheckBase(ManagedTemporaryBase privateBase)
    {
        if (OperatingSystem.IsWindows())
            return null;

        try
        {
            return CreateAndCheckFolder(privateBase.Value, HostManagedTemporaryFolderInspector.Instance) is { } refusal
                ? new ManagedTemporaryBaseRefusal(refusal, CouldNotUseFilesystem: false)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return new ManagedTemporaryBaseRefusal(
                $"Error preparing managed temporary directory: {ex.Message}",
                CouldNotUseFilesystem: true);
        }
    }

    // Each check reads the folder without following a link: not a link, owned by the daemon user,
    // and no group or other permission bit. The path-access mutation gate
    // (scripts/run-path-access-mutations.sh) mutates this method, from its first check to the
    // marker comment on the last line.
    private static string? CheckFolder(string folder, IManagedTemporaryFolderInspector inspector)
    {
        if (!inspector.TryInspect(folder, out var facts))
            return $"Error: The managed temporary folder '{folder}' does not exist after it was created.";

        if (facts.IsSymbolicLink)
            return $"Error: The managed temporary folder '{folder}' is a symbolic link. The operation was refused.";

        if (facts.OwnerUserId != inspector.EffectiveUserId)
            return $"Error: The managed temporary folder '{folder}' is owned by user {facts.OwnerUserId}, not by the daemon user {inspector.EffectiveUserId}. The operation was refused.";

        if ((facts.Mode & GroupAndOtherBits) != 0)
            return $"Error: The managed temporary folder '{folder}' allows group or other access. The operation was refused.";

        return null; // the folder passed every check
    }
}

/// <summary>Why <see cref="ManagedTemporaryEnvironment.CheckBase"/> refused the base.</summary>
/// <param name="Reason">The refusal text the shell launch would report.</param>
/// <param name="CouldNotUseFilesystem">True when creating or reaching the folder failed (for example a read-only
/// filesystem); false when the folder exists but is a link, has another owner, or allows group or other access.</param>
internal sealed record ManagedTemporaryBaseRefusal(string Reason, bool CouldNotUseFilesystem);

/// <summary>
/// Reads the facts of one folder without following a link at the final path segment
/// (<c>lstat</c>). The private-folder checks of <see cref="ManagedTemporaryEnvironment"/> use it.
/// </summary>
internal interface IManagedTemporaryFolderInspector
{
    /// <summary>Gets the effective user ID of the daemon process.</summary>
    uint EffectiveUserId { get; }

    /// <summary>Reads the facts of a path. Returns <c>false</c> when the path does not exist.</summary>
    bool TryInspect(string path, out ManagedTemporaryFolderFacts facts);
}

/// <summary>The facts of one folder that the private-folder checks need.</summary>
/// <param name="IsSymbolicLink">True when the path itself is a symbolic link.</param>
/// <param name="OwnerUserId">The user ID that owns the path.</param>
/// <param name="Mode">The permission bits of the path.</param>
internal readonly record struct ManagedTemporaryFolderFacts(
    bool IsSymbolicLink,
    uint OwnerUserId,
    UnixFileMode Mode);

/// <summary>Reads folder facts from the host filesystem with a native call that does not follow links.</summary>
/// <remarks>
/// .NET has no owner API and no <c>lstat</c> that reports the user ID. The class calls
/// <c>statx</c> on Linux and <c>lstat</c> on macOS through P/Invoke. Each call reads a fixed offset
/// of a native buffer: a wrong offset would silently report a wrong owner or mode, so every
/// offset below is the documented layout of one platform ABI. A failed call throws an
/// <see cref="IOException"/> that names the path and the errno text. The caller then refuses the
/// shell launch. There is no fallback to a managed approximation.
/// </remarks>
internal sealed partial class HostManagedTemporaryFolderInspector : IManagedTemporaryFolderInspector
{
    internal static HostManagedTemporaryFolderInspector Instance { get; } = new();

    // errno values that are equal on Linux and macOS.
    private const int ENOENT = 2;

    // S_IFMT and S_IFLNK from <sys/stat.h>. Both platforms use these values.
    private const uint FileTypeMask = 0xF000;
    private const uint SymbolicLinkType = 0xA000;

    // Permission bits plus setuid, setgid, and sticky. UnixFileMode uses the same bit values.
    private const uint ModeBitsMask = 0xFFF;

    // Linux: <linux/fcntl.h> and <linux/stat.h>.
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const uint StatxMode = 0x2;
    private const uint StatxUid = 0x8;

    // struct statx is 256 bytes with one layout on every Linux architecture:
    // stx_mask u32 @0, stx_uid u32 @20, stx_mode u16 @28.
    private const int StatxSize = 256;
    private const int StatxMaskOffset = 0;
    private const int StatxUidOffset = 20;
    private const int StatxModeOffset = 28;

    // macOS: struct stat with 64-bit inode numbers (<sys/stat.h>): st_mode u16 @4, st_uid u32 @16.
    // The buffer is larger than struct stat (144 bytes), so a newer layout cannot write outside it.
    private const int DarwinStatSize = 256;
    private const int DarwinModeOffset = 4;
    private const int DarwinUidOffset = 16;

    private HostManagedTemporaryFolderInspector()
    {
    }

    public uint EffectiveUserId => PosixUserId.GetEffective();

    public bool TryInspect(string path, out ManagedTemporaryFolderFacts facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("The path must not contain a NUL character.", nameof(path));

        try
        {
            if (OperatingSystem.IsLinux())
                return TryInspectLinux(path, out facts);

            if (OperatingSystem.IsMacOS())
                return TryInspectMacOs(path, out facts);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A libc without the symbol must refuse the launch with a clear error, not fall back.
            throw new IOException($"The native folder check is not available on this host: {ex.Message}", ex);
        }

        throw new PlatformNotSupportedException(
            "The private managed temporary folder checks support Linux and macOS only.");
    }

    private static bool TryInspectLinux(string path, out ManagedTemporaryFolderFacts facts)
    {
        facts = default;
        Span<byte> buffer = stackalloc byte[StatxSize];
        buffer.Clear();

        // AT_SYMLINK_NOFOLLOW: if the last segment is a link, report the link and not its target.
        var result = statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType | StatxMode | StatxUid, buffer);
        if (result != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ENOENT)
                return false;

            throw NativeFailure("statx", path, errno);
        }

        // The kernel sets a bit in stx_mask for each field that it filled. A missing bit means the
        // file system gave no answer for that field, and a zero would read as "root owns it".
        var required = StatxType | StatxMode | StatxUid;
        if ((BitConverter.ToUInt32(buffer[StatxMaskOffset..]) & required) != required)
            throw new IOException($"The file system did not report the owner and mode of '{path}'.");

        facts = ToFacts(
            BitConverter.ToUInt16(buffer[StatxModeOffset..]),
            BitConverter.ToUInt32(buffer[StatxUidOffset..]));
        return true;
    }

    private static bool TryInspectMacOs(string path, out ManagedTemporaryFolderFacts facts)
    {
        facts = default;
        Span<byte> buffer = stackalloc byte[DarwinStatSize];
        buffer.Clear();

        // lstat reports a link and does not follow it. On x86_64 the plain symbol is the old
        // 32-bit-inode layout, so that architecture uses the $INODE64 symbol. arm64 has one layout.
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? lstatInode64(path, buffer)
            : lstat(path, buffer);
        if (result != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ENOENT)
                return false;

            throw NativeFailure("lstat", path, errno);
        }

        facts = ToFacts(
            BitConverter.ToUInt16(buffer[DarwinModeOffset..]),
            BitConverter.ToUInt32(buffer[DarwinUidOffset..]));
        return true;
    }

    private static ManagedTemporaryFolderFacts ToFacts(uint rawMode, uint ownerUserId)
        => new(
            (rawMode & FileTypeMask) == SymbolicLinkType,
            ownerUserId,
            (UnixFileMode)(rawMode & ModeBitsMask));

    private static IOException NativeFailure(string call, string path, int errno)
        => new($"The call {call} failed for '{path}': {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).");

    // glibc 2.28 and musl 1.2.5 export statx.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat(string path, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstatInode64(string path, Span<byte> buffer);
}
