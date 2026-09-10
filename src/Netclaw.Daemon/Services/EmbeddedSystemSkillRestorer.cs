// -----------------------------------------------------------------------
// <copyright file="EmbeddedSystemSkillRestorer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Restores the system skill tree that the daemon assembly owns.
/// </summary>
internal static class EmbeddedSystemSkillRestorer
{
    private const string ResourcePrefix = "Netclaw.SystemSkills";
    private const string ExecutablePathsResourceName = "Netclaw.SystemSkillExecutablePaths";
    private const UnixFileMode ReadOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                  | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecutableFileMode = ReadOnlyFileMode | UnixFileMode.UserExecute
                                                     | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    internal static void Restore(NetclawPaths paths)
        => Restore(paths, typeof(EmbeddedSystemSkillRestorer).Assembly);

    internal static void Restore(NetclawPaths paths, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(assembly);

        var systemDirectory = Path.GetFullPath(paths.SystemSkillsDirectory);
        var skillsDirectory = Path.GetFullPath(paths.SkillsDirectory);
        ValidateManagedDirectory(paths, skillsDirectory, systemDirectory);

        var resources = assembly.GetManifestResourceNames()
            .Where(IsSystemSkillResource)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0)
            throw new InvalidOperationException("The daemon assembly does not contain system skill resources.");

        var executablePaths = ReadExecutablePaths(assembly, resources);

        var stagingDirectory = Path.Combine(skillsDirectory, $".system.staging-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(skillsDirectory, $".system.backup-{Guid.NewGuid():N}");
        var movedCurrentTree = false;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (var resource in resources)
                WriteResource(assembly, resource, stagingDirectory, executablePaths);

            if (Directory.Exists(systemDirectory))
            {
                Directory.Move(systemDirectory, backupDirectory);
                movedCurrentTree = true;
            }

            Directory.Move(stagingDirectory, systemDirectory);
        }
        catch
        {
            RestorePreviousTree(systemDirectory, backupDirectory, movedCurrentTree);
            throw;
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingDirectory);
        }

        if (movedCurrentTree)
            Directory.Delete(backupDirectory, recursive: true);
    }

    private static void ValidateManagedDirectory(
        NetclawPaths paths,
        string skillsDirectory,
        string systemDirectory)
    {
        var baseDirectory = Path.GetFullPath(paths.BasePath);
        var expectedSystemDirectory = Path.Combine(skillsDirectory, ".system");
        if (!PathUtility.IsWithinRoot(systemDirectory, baseDirectory)
            || !string.Equals(systemDirectory, expectedSystemDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The system skill directory must remain below the Netclaw home skills directory.");
        }

        RejectSymbolicLink(skillsDirectory);
        RejectSymbolicLink(systemDirectory);
    }

    private static void WriteResource(
        Assembly assembly,
        string resourceName,
        string stagingDirectory,
        IReadOnlySet<string> executablePaths)
    {
        var relativePath = GetResourceRelativePath(resourceName).Replace('/', Path.DirectorySeparatorChar);
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidOperationException($"The embedded system skill path is invalid: {resourceName}.");

        var targetPath = Path.GetFullPath(Path.Combine(stagingDirectory, relativePath));
        if (!PathUtility.IsWithinRoot(targetPath, stagingDirectory))
            throw new InvalidOperationException($"The embedded system skill path escapes its staging directory: {resourceName}.");

        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded system skill resource is unavailable: {resourceName}.");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        resource.CopyTo(target);

        if (!OperatingSystem.IsWindows())
        {
            var canonicalPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            var mode = executablePaths.Contains(canonicalPath) ? ExecutableFileMode : ReadOnlyFileMode;
            File.SetUnixFileMode(targetPath, mode);
        }
    }

    private static IReadOnlySet<string> ReadExecutablePaths(
        Assembly assembly,
        IReadOnlyCollection<string> resources)
    {
        using var stream = assembly.GetManifestResourceStream(ExecutablePathsResourceName)
            ?? throw new InvalidOperationException("The daemon assembly does not contain the system skill executable path manifest.");
        var manifest = JsonSerializer.Deserialize<string[]>(stream)
            ?? throw new InvalidOperationException("The system skill executable path manifest is invalid.");
        var expectedPaths = resources
            .Select(GetResourceRelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var executablePaths = manifest.ToHashSet(StringComparer.Ordinal);
        if (executablePaths.Count != manifest.Length || !executablePaths.IsSubsetOf(expectedPaths))
        {
            throw new InvalidOperationException("The system skill executable path manifest does not match the embedded skill tree.");
        }

        return executablePaths;
    }

    internal static bool IsSystemSkillResource(string resourceName)
        => resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
           && resourceName.Length > ResourcePrefix.Length
           && resourceName[ResourcePrefix.Length] is '/' or '\\';

    internal static string GetResourceRelativePath(string resourceName)
    {
        if (!IsSystemSkillResource(resourceName))
            throw new ArgumentException("The resource is not a system skill resource.", nameof(resourceName));

        return resourceName[(ResourcePrefix.Length + 1)..].Replace('\\', '/');
    }

    private static void RestorePreviousTree(
        string systemDirectory,
        string backupDirectory,
        bool movedCurrentTree)
    {
        if (!movedCurrentTree || !Directory.Exists(backupDirectory))
            return;

        if (Directory.Exists(systemDirectory))
            Directory.Delete(systemDirectory, recursive: true);

        Directory.Move(backupDirectory, systemDirectory);
    }

    private static void DeleteDirectoryIfPresent(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void RejectSymbolicLink(string path)
    {
        if (Directory.Exists(path)
            && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"The system skill directory cannot be a symbolic link: {path}.");
        }
    }

    private static bool IsSafeRelativePath(string path)
        => !Path.IsPathRooted(path)
           && path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .All(static segment => segment is not "" and not "." and not "..");
}
