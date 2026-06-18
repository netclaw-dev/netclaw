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
    /// are created as needed. When <paramref name="hardenTempPermissions"/> is supplied it runs on
    /// the temporary file BEFORE the rename, so the destination is never momentarily exposed with
    /// looser permissions than the final file.
    /// </summary>
    public static void WriteAllText(string path, string contents, Action<string>? hardenTempPermissions = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Temp lives in the same directory as the destination so File.Move is a same-filesystem
        // atomic rename rather than a copy+delete (which would not be atomic across volumes).
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
