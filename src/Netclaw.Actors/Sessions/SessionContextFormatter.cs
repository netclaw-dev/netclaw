// -----------------------------------------------------------------------
// <copyright file="SessionContextFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>Formats the shared model-visible session storage context for parent and child agents.</summary>
internal static class SessionContextFormatter
{
    internal static string Format(SessionStoragePaths storage, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var idLine = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : $"\nid: {sessionId}";
        return $"[session]{idLine}\n" +
               $"session_dir: {storage.SessionDirectory}\n" +
               $"temp_dir: {storage.ManagedTemporary.Directory}\n" +
               $"artifact_dir: {storage.ArtifactDirectory}\n" +
               $"worktree_dir: {storage.WorktreeDirectory}\n" +
               $"log_path: {storage.LogPath}\n" +
               ToolChoiceGuidance.StructuredWorkspaceSelection + "\n" +
               ToolChoiceGuidance.DirectorySelectionOrder + "\n" +
               ToolChoiceGuidance.ShellCompositionOrder + "\n" +
               "temp_dir is private managed temporary storage for disposable files. " +
               "session_dir is the default workspace when no project is active. " +
               "Use an explicitly required platform temporary path unchanged. " +
               "Netclaw does not automatically clean managed temporary storage yet.";
    }
}
