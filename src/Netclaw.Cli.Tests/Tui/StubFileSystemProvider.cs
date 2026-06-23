// -----------------------------------------------------------------------
// <copyright file="StubFileSystemProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Layout;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// In-memory <see cref="IFileSystemProvider"/> for driving the directory picker deterministically
/// in headless tests without touching the real filesystem.
/// </summary>
internal sealed class StubFileSystemProvider : IFileSystemProvider
{
    private readonly Dictionary<string, IReadOnlyList<FileSystemEntry>> _entries;
    private readonly HashSet<string> _existing;

    public StubFileSystemProvider(
        IEnumerable<string>? existingDirectories = null,
        IReadOnlyDictionary<string, IReadOnlyList<FileSystemEntry>>? entries = null)
    {
        _existing = new HashSet<string>(existingDirectories ?? [], StringComparer.Ordinal);
        _entries = entries is null
            ? new Dictionary<string, IReadOnlyList<FileSystemEntry>>(StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<FileSystemEntry>>(entries, StringComparer.Ordinal);

        // A directory we can enumerate necessarily exists.
        foreach (var key in _entries.Keys)
            _existing.Add(key);
    }

    public IReadOnlyList<FileSystemEntry> GetEntries(string directoryPath)
        => _entries.TryGetValue(directoryPath, out var entries) ? entries : [];

    public bool DirectoryExists(string path) => _existing.Contains(path);

    public string? GetParentDirectory(string path) => Path.GetDirectoryName(path);

    public static FileSystemEntry Dir(string fullPath)
        => new(Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)), fullPath, IsDirectory: true, null, null);
}
