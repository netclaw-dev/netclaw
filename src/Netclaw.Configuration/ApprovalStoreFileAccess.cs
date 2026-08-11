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
        return File.ReadAllBytes(path);
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

            File.Move(tempPath, path, overwrite: true);
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

            File.Move(tempPath, path, overwrite: true);
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
