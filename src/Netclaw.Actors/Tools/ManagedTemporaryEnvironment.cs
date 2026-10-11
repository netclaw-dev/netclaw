// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
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
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        try
        {
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

            Directory.CreateDirectory(normalizedTemporaryDirectory);
            if (!Directory.Exists(normalizedTemporaryDirectory))
                return $"Error: Managed temporary directory '{normalizedTemporaryDirectory}' was not created.";

            if (FileSystemAuthority.EvaluateMembership(temporary, storage) is not PathDecision.Allowed)
                return "Error: The managed temporary directory contains an unsafe filesystem link.";

            // The shell parser resolves these variables from the same values (ShellExecutionEnvironment.CreateLaunchEnvironment).
            ShellExecutionEnvironment.ApplyTemporaryVariables(startInfo.Environment, location);
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
}
