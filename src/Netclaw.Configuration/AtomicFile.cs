// -----------------------------------------------------------------------
// <copyright file="AtomicFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.InteropServices;

namespace Netclaw.Configuration;

/// <summary>
/// Atomic file writes for config, secrets, and the paired-device registry. Content is written
/// to a sibling temporary file, flushed to disk, optionally permission-hardened, and then renamed
/// over the destination. The rename is atomic on POSIX (<c>rename(2)</c>) and Windows
/// (<c>MoveFileEx</c> replace), so a crash, an interrupted write, or a concurrent reader can never
/// observe a partially-written or truncated file — the destination is always either the old content
/// or the complete new content. Last-writer-wins between two concurrent writers is acceptable
/// (callers that must not race serialize their writes separately); this type only guarantees that
/// no write ever corrupts the file.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/> atomically. Parent directories
    /// are created as needed. The temporary file gets the mode of the file that it replaces, or the
    /// default mode of a new file when none exists, so a rewrite is never more open than the file
    /// before it. <paramref name="hardenTempPermissions"/> runs on the temporary file BEFORE the
    /// rename and can set the mode of the final file, for example a copy of the replaced mode.
    /// A file that holds plain-text values or credentials uses <see cref="WriteAllTextOwnerOnly"/>.
    /// </summary>
    public static void WriteAllText(string path, string contents, Action<string>? hardenTempPermissions = null)
    {
        Write(path, contents, hardenTempPermissions, ModeOfExistingFile(path));
    }

    // A link to a missing file counts as no file: the write replaces the link.
    private static UnixFileMode? ModeOfExistingFile(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return null;

        try
        {
            return File.GetUnixFileMode(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/> atomically, with mode 0600 on Linux
    /// and macOS. The temporary file is created owner-only, so the plain text is never readable by group
    /// or others, not even before the rename. Only daemon.env, secrets.json, and devices.json use this
    /// method. Any other file keeps the default mode, because a container can run the daemon as another
    /// user than the owner of a bind-mounted file.
    /// </summary>
    public static void WriteAllTextOwnerOnly(string path, string contents)
        => Write(path, contents, HardenOwnerOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    private static void Write(string path, string contents, Action<string>? hardenTempPermissions, UnixFileMode? createMode)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Temp lives in the same directory as the destination so File.Move is a same-filesystem
        // atomic rename rather than a copy+delete (which would not be atomic across volumes).
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };

            // The mode is part of the create call: a chmod after the write leaves a window in which
            // another user can read the plain text.
            if (createMode is { } mode && !OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = mode;

            using (var stream = new FileStream(temp, streamOptions))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true); // fsync the new content before it replaces the old file
            }

            hardenTempPermissions?.Invoke(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Failure before the rename leaves the destination untouched; clean up the temp so a
            // partial write never lingers next to the real file. Cleanup is best-effort — a delete
            // failure must not mask the original write exception, which we rethrow.
            TryDeleteTemp(temp);
            throw;
        }
    }

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/> atomically.
    /// The method observes cancellation before it replaces the destination.
    /// </summary>
    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        Action<string> hardenTempPermissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hardenTempPermissions);
        cancellationToken.ThrowIfCancellationRequested();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var stream = new FileStream(temp, streamOptions))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            hardenTempPermissions(temp);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }
    }

    // Deletes a leftover temp file, returning whether it succeeded. The expected IO/access failures
    // are turned into a false result rather than propagating, so a failed cleanup never masks a more
    // important exception that is already in flight at the call site.
    private static bool TryDeleteTemp(string temp)
    {
        if (!File.Exists(temp))
            return true;

        try
        {
            File.Delete(temp);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gives <paramref name="temp"/> the permission bits of <paramref name="source"/> when that file
    /// exists (Linux/macOS). Pass it as the harden callback so a rewrite does not widen the mode of a
    /// file an operator made owner-only, for example a netclaw.json that holds a token.
    /// </summary>
    public static void CopyUnixMode(string source, string temp)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(source))
            File.SetUnixFileMode(temp, File.GetUnixFileMode(source));
    }

    /// <summary>
    /// Restrict a file to owner-only read/write (chmod 600) on Linux/macOS; a no-op on Windows,
    /// which relies on user-profile ACLs. Pass as the harden callback to <see cref="WriteAllText"/>
    /// when writing secrets.json or devices.json so those files are never group/world-readable.
    /// </summary>
    public static void HardenOwnerOnly(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
