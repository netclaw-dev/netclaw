// -----------------------------------------------------------------------
// <copyright file="ScopedFileAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ScopedFileAccessPolicy
{
    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly Lazy<IReadOnlyList<string>> _cachedGlobalReadRoots;
    private readonly Lazy<string?> _cachedWorkspacesRoot;

    // paths is required (not nullable): the workspaces/global-read roots are
    // sourced from it, and a null would silently drop them — the exact silent
    // fallback that let autonomous workspace access break unnoticed (#1493).
    public ScopedFileAccessPolicy(ToolConfig toolConfig, NetclawPaths paths)
    {
        _profileResolver = new ToolAudienceProfileResolver(toolConfig, paths);
        _cachedGlobalReadRoots = new Lazy<IReadOnlyList<string>>(() =>
            _profileResolver.ResolveGlobalReadRoots()
                .Select(PathUtility.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        _cachedWorkspacesRoot = new Lazy<string?>(() =>
        {
            var workspaces = _profileResolver.ResolveWorkspacesDirectory();
            return string.IsNullOrWhiteSpace(workspaces) ? null : PathUtility.Normalize(workspaces);
        });
    }

    public bool TryResolveReadPath(string rawPath, ToolInvocationContext context, out string fullPath, out string error)
        => TryResolvePath(rawPath, context, AccessKind.Read, out fullPath, out error);

    public bool TryResolveWritePath(string rawPath, ToolInvocationContext context, out string fullPath, out string error)
        => TryResolvePath(rawPath, context, AccessKind.Write, out fullPath, out error);

    public bool TryResolveAttachPath(string rawPath, ToolInvocationContext context, out string fullPath, out string error)
        => TryResolvePath(rawPath, context, AccessKind.Attach, out fullPath, out error);

    public IReadOnlyList<string> GetRootsForContext(ToolInvocationContext context, AccessKind accessKind)
    {
        var profile = _profileResolver.ResolveProfile(context);
        var access = GetAccessProfile(profile, accessKind);
        return ResolveAndMergeRoots(access, context, context.Audience, accessKind);
    }

    private bool TryResolvePath(
        string rawPath,
        ToolInvocationContext context,
        AccessKind accessKind,
        out string fullPath,
        out string error)
    {
        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            error = $"Error: Invalid path: {ex.Message}";
            return false;
        }

        var profile = _profileResolver.ResolveProfile(context);
        var access = GetAccessProfile(profile, accessKind);

        if (access.Mode == ToolFilesystemMode.All)
        {
            // Autonomous (non-interactive) channels have no human approval backstop,
            // so an unrestricted audience is confined to the autonomous zone
            // (session + project + operator-configured roots) instead of being
            // granted blanket filesystem access. Interactive channels keep the
            // blanket grant — the live approval gate is their backstop. This is the
            // single seam that covers shell (via TryResolveWritePath) and every file
            // tool at once.
            if (context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Unavailable)
                return TryResolveWithinAutonomousZone(fullPath, context, accessKind, out error);

            error = string.Empty;
            return true;
        }

        var audience = context.Audience;
        var label = GetAudienceLabel(audience);

        if (access.Mode == ToolFilesystemMode.None)
        {
            error = $"Error: {label} trust context does not allow {accessKind.ToString().ToLowerInvariant()} access to local files.";
            return false;
        }

        var roots = ResolveAndMergeRoots(access, context, audience, accessKind);

        if (roots.Count == 0)
        {
            error = $"Error: {label} trust context does not have any configured local file roots for {accessKind.ToString().ToLowerInvariant()} access.";
            return false;
        }

        foreach (var root in roots)
        {
            if (!PathUtility.IsWithinRoot(fullPath, root))
                continue;

            if (PathUtility.ContainsSymlinkSegment(root, fullPath))
            {
                error = $"Error: {label} trust context may not access files through symlinked paths inside the current session directory or configured roots.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        error = audience == TrustAudience.Public
            ? $"Error: {label} trust context may only access files inside the current session directory."
            : $"Error: {label} trust context may only access files inside the current session directory or configured roots: {string.Join(", ", roots)}.";
        return false;
    }

    private static ToolFilesystemAccessProfile GetAccessProfile(ToolAudienceProfile profile, AccessKind accessKind) =>
        accessKind switch
        {
            AccessKind.Read => profile.ReadFiles,
            AccessKind.Write => profile.WriteFiles,
            AccessKind.Attach => profile.AttachFiles,
            _ => profile.ReadFiles
        };

    /// <summary>
    /// Resolves profile roots and merges global read roots for read access.
    /// Single source of truth for root resolution — used by both
    /// <see cref="GetRootsForContext"/> and <see cref="TryResolvePath"/>.
    /// Public audience is excluded from global read roots (skills, identity,
    /// workspaces) — it may only access its session directory.
    /// </summary>
    private IReadOnlyList<string> ResolveAndMergeRoots(
        ToolFilesystemAccessProfile access,
        ToolInvocationContext context,
        TrustAudience audience,
        AccessKind accessKind)
    {
        var roots = _profileResolver.ResolveRoots(access, context)
            .Select(PathUtility.Normalize)
            .ToList();

        if (accessKind == AccessKind.Read && audience != TrustAudience.Public)
        {
            foreach (var globalRoot in _cachedGlobalReadRoots.Value)
                roots.Add(globalRoot);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Confines an autonomous (non-interactive) session whose audience would
    /// otherwise grant unrestricted (<see cref="ToolFilesystemMode.All"/>) access to
    /// the autonomous zone. Fails closed when the zone is empty (the session
    /// directory is normally always present, so this is a defensive guard).
    /// </summary>
    private bool TryResolveWithinAutonomousZone(
        string fullPath,
        ToolInvocationContext context,
        AccessKind accessKind,
        out string error)
    {
        var zone = ResolveAutonomousZone(context, accessKind);
        if (zone.Count == 0)
        {
            error = "Error: autonomous session has no accessible file roots.";
            return false;
        }

        foreach (var root in zone)
        {
            if (!PathUtility.IsWithinRoot(fullPath, root))
                continue;

            if (PathUtility.ContainsSymlinkSegment(root, fullPath))
            {
                error = "Error: autonomous session may not access files through symlinked paths inside its zone.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        error = "Error: autonomous session may only access files inside its session directory, project directory, or configured autonomous roots.";
        return false;
    }

    /// <summary>
    /// Resolves the autonomous filesystem zone from the data already on the
    /// execution context: the per-session directory and the current project
    /// directory, always present for both reads and writes. Read access
    /// additionally includes the non-sensitive global read roots (skills,
    /// identity, workspaces). Write/attach access additionally includes the
    /// configured <em>workspaces</em> directory only — the operator's designated
    /// writable working area — but NOT skills/identity, which are system-managed
    /// (an autonomous session must never rewrite its own identity or skills).
    /// Plain file writes are not gated by the interactive approval system, so
    /// confining them to session+project blocked legitimate cross-run state in
    /// the workspace without a security benefit. No additional plumbing — the
    /// cached read roots and workspaces root already exist on this policy.
    /// </summary>
    private IReadOnlyList<string> ResolveAutonomousZone(ToolInvocationContext context, AccessKind accessKind)
    {
        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(context.SessionDirectory);

        if (!string.IsNullOrWhiteSpace(context.ProjectDirectory))
            roots.Add(context.ProjectDirectory);

        if (accessKind == AccessKind.Read)
            roots.AddRange(_cachedGlobalReadRoots.Value);
        else if (_cachedWorkspacesRoot.Value is { } workspacesRoot)
            roots.Add(workspacesRoot);

        return roots
            .Select(PathUtility.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetAudienceLabel(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => "Public",
        TrustAudience.Team => "Team",
        TrustAudience.Personal => "Personal",
        _ => "Public"
    };

    internal enum AccessKind
    {
        Read,
        Write,
        Attach
    }
}
