// -----------------------------------------------------------------------
// <copyright file="TestSessionTempDirectory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Owns a uniquely-named temp directory for the lifetime of a test and
/// deletes the whole tree (including any locked SQLite files) when disposed.
/// This is the single canonical way for tests to obtain a throwaway
/// <see cref="NetclawPaths"/> root, so tests cannot accidentally leave
/// thousands of <c>/tmp/netclaw-*</c> directories behind (see issue #2266).
/// </summary>
public sealed class TestSessionTempDirectory : IAsyncDisposable
{
    public string Path { get; }

    public NetclawPaths Paths { get; }

    private TestSessionTempDirectory(NetclawPaths paths)
    {
        Paths = paths;
        Path = paths.BasePath;
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Creates a <see cref="NetclawPaths"/> rooted at a unique temp directory,
    /// with the directory tree owned (and later deleted) by this helper.
    /// <paramref name="prefix"/> must be a single safe file-name segment (no
    /// path separators, not rooted, no traversal) so the owned tree always
    /// stays under <see cref="Path.GetTempPath()"/>.
    /// </summary>
    public static TestSessionTempDirectory Create(
        string prefix = "netclaw-test-",
        bool createDirectoryTree = false)
    {
        ValidatePrefix(prefix);

        var basePath = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            $"{prefix}{Guid.NewGuid():N}");

        var paths = new NetclawPaths(basePath);
        if (createDirectoryTree)
            paths.EnsureDirectoriesExist();

        return new TestSessionTempDirectory(paths);
    }

    private static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            throw new ArgumentException("A non-empty prefix is required.", nameof(prefix));

        var leaf = System.IO.Path.GetFileName(prefix);
        if (!string.Equals(leaf, prefix, StringComparison.Ordinal)
            || System.IO.Path.IsPathRooted(prefix)
            || prefix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || prefix.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The prefix must be a single file-name segment (no separators, root, traversal, or invalid name chars).",
                nameof(prefix));
        }
    }

    /// <summary>
    /// Deletes the owned temp directory tree. <see cref="DisposableTempDir.Delete"/>
    /// retries when a file is still locked, and clears the SQLite connection
    /// pools only after a delete attempt fails.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        DisposableTempDir.Delete(Path);
        return ValueTask.CompletedTask;
    }
}
