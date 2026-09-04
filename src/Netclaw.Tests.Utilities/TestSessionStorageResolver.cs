// -----------------------------------------------------------------------
// <copyright file="TestSessionStorageResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Resolves deterministic legacy session paths for tests that do not exercise storage binding.
/// </summary>
internal sealed class TestSessionStorageResolver(
    NetclawPaths paths,
    string? sessionLogsDirectory = null) : ISessionStorageResolver
{
    private static readonly NetclawPaths SharedPaths = new(Path.Combine(
        Path.GetTempPath(),
        "netclaw-test-session-storage"));

    /// <summary>
    /// Gets the shared resolver for tests that do not need isolated paths.
    /// </summary>
    internal static TestSessionStorageResolver Instance { get; } = new(SharedPaths);

    /// <inheritdoc />
    public SessionStoragePaths Resolve(SessionId sessionId)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        return SessionStoragePaths.CreateLegacy(
            SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory),
            sessionLogsDirectory ?? paths.SessionLogsDirectory,
            sanitized);
    }
}
