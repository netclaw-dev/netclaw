// -----------------------------------------------------------------------
// <copyright file="ApprovalStoreFileAccess.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Diagnostics;

namespace Netclaw.Configuration;

internal interface IApprovalStoreFileAccess
{
    FileStream AcquireLock(string lockPath, TimeSpan timeout);

    byte[] ReadAllBytes(string path);

    void WriteAtomic(string path, string contents, byte[]? expectedSourceBytes);

    void ReplaceVersion2(string path, string backupPath, byte[] sourceBytes, string version3Contents);

    void EnsureNotLink(string path);
}

internal sealed class ApprovalStoreFileAccess : IApprovalStoreFileAccess
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    // A read races only one rename, so it needs few attempts.
    private const int ReadAttempts = 3;
    private const int ReadRetryDelayMilliseconds = 5;

    // A rename waits for each reader that holds the file. About one second.
    private const int MoveAttempts = 40;
    private const int MoveRetryDelayMilliseconds = 25;

    internal static ApprovalStoreFileAccess Instance { get; } = new();

    private ApprovalStoreFileAccess()
    {
    }

    public FileStream AcquireLock(string lockPath, TimeSpan timeout)
    {
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        while (true)
        {
            try
            {
                EnsureNotLink(lockPath);
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                try
                {
                    EnsureNotLink(lockPath);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.LockUnavailable,
                    "The approval store lock was not available.",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.LockUnavailable,
                    "The approval store lock was not available.",
                    ex);
            }
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        EnsureNotLink(path);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ReadShared(path);
            }
            catch (Exception ex) when (attempt < ReadAttempts && IsTransientWindowsShareFailure(ex))
            {
                // A read takes no store lock, so it can meet a rename that a
                // writer in another process does on this path. Windows can
                // refuse the open for the short time of that rename. The
                // retry is small because the rename is short.
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }
    }

    // FileShare.ReadWrite | FileShare.Delete: a writer in another process
    // must be able to rename a new file over this path while the read holds
    // it open. The handle refers to one complete file. Writers never change
    // a file in place; they write a temporary file and rename it.
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Renames <paramref name="sourcePath"/> to <paramref name="destinationPath"/>.
    /// On Windows, the method retries a short time when an open handle on the
    /// destination blocks the rename.
    /// </summary>
    /// <remarks>
    /// .NET <see cref="File.Move(string, string, bool)"/> calls
    /// <c>MoveFileExW</c> with <c>MOVEFILE_REPLACE_EXISTING</c>. That call does
    /// not use POSIX rename semantics. NTFS then refuses to replace a target
    /// that has an open handle, whatever the share mode of that handle
    /// (FILE_RENAME_INFORMATION, "Special rules for renaming open files").
    /// Approval reads take no store lock, so a reader in another process can
    /// hold the target for the short time of its read. The caller holds the
    /// store lock, so only readers can block the rename, and each reader
    /// closes its handle quickly. Linux and macOS rename over open files, so
    /// they do not retry.
    /// </remarks>
    internal static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && IsTransientWindowsShareFailure(ex))
            {
                Thread.Sleep(MoveRetryDelayMilliseconds);
            }
        }
    }

    // Windows reports an open target as ERROR_ACCESS_DENIED
    // (UnauthorizedAccessException) or as ERROR_SHARING_VIOLATION or
    // ERROR_LOCK_VIOLATION (IOException). A real permission error also shows
    // as ERROR_ACCESS_DENIED. The retry budget is small, so such an error
    // still fails after a short delay.
    private static bool IsTransientWindowsShareFailure(Exception ex)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return ex is UnauthorizedAccessException ||
               (ex is IOException && (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation);
    }

    public void WriteAtomic(
        string path,
        string contents,
        byte[]? expectedSourceBytes)
    {
        EnsureNotLink(path);
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteNewFile(tempPath, Encoding.UTF8.GetBytes(contents));
            EnsureNotLink(path);
            if (!SourceMatches(path, expectedSourceBytes))
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.IoFailure,
                    "The approval store changed before replace.");
            }

            MoveFile(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteNewFile(tempPath);
            throw;
        }
    }

    private static bool SourceMatches(string path, byte[]? expectedSourceBytes)
    {
        if (expectedSourceBytes is null)
        {
            return !File.Exists(path);
        }

        return File.Exists(path) &&
               File.ReadAllBytes(path).AsSpan().SequenceEqual(expectedSourceBytes);
    }

    public void ReplaceVersion2(
        string path,
        string backupPath,
        byte[] sourceBytes,
        string version3Contents)
    {
        EnsureNotLink(path);
        EnsureNotLink(backupPath);
        if (File.Exists(backupPath))
        {
            if (!File.ReadAllBytes(backupPath).AsSpan().SequenceEqual(sourceBytes))
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.MigrationFailed,
                    "The version-2 backup does not match the active source.");
            }
        }
        else
        {
            WriteNewFile(backupPath, sourceBytes);
        }

        var tempPath = path + ".v3.tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteNewFile(tempPath, Encoding.UTF8.GetBytes(version3Contents));
            EnsureNotLink(path);
            var currentBytes = File.ReadAllBytes(path);
            if (!currentBytes.AsSpan().SequenceEqual(sourceBytes))
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.MigrationFailed,
                    "The approval store changed before version-3 replace.");
            }

            MoveFile(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteNewFile(tempPath);
            throw;
        }
    }

    public void EnsureNotLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.IoFailure,
                    "An approval store path must not be a symbolic link.");
            }
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
    }

    private void WriteNewFile(string path, byte[] contents)
    {
        EnsureNotLink(path);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private void TryDeleteNewFile(string path)
    {
        try
        {
            EnsureNotLink(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ApprovalStoreException)
        {
            Debug.WriteLine($"Approval store temporary-file cleanup failed: {ex.Message}");
        }
    }
}
