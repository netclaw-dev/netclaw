// -----------------------------------------------------------------------
// <copyright file="PathAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Owns canonical path resolution and file-protection decisions.
/// </summary>
internal sealed class PathAccessPolicy
{
    /// <summary>Classifies why a path access decision failed.</summary>
    internal enum PathAccessFailure
    {
        /// <summary>The caller did not supply a valid path.</summary>
        InvalidInput,

        /// <summary>The path or operation is outside the caller's authority.</summary>
        AccessDenied,

        /// <summary>A relative path has no valid project or session base.</summary>
        MissingBase
    }

    /// <summary>Identifies the filesystem operation that needs authority.</summary>
    internal enum FileOperation
    {
        /// <summary>Reads, lists, or searches filesystem content.</summary>
        Read,

        /// <summary>Creates or changes filesystem content.</summary>
        Write,

        /// <summary>Returns an existing file through a channel.</summary>
        Attach,

        /// <summary>Validates a proposed project root without granting broader filesystem reach.</summary>
        DeclareProjectScope
    }

    /// <summary>Returns the canonical path and the typed result of one access check.</summary>
    internal sealed record PathAccessDecision
    {
        private PathAccessDecision(
            bool allowed,
            string canonicalPath,
            string error,
            PathAccessFailure? failure)
        {
            Allowed = allowed;
            CanonicalPath = canonicalPath;
            Error = error;
            Failure = failure;
        }

        /// <summary>Gets whether the policy allowed the operation.</summary>
        public bool Allowed { get; }

        /// <summary>Gets the canonical path when path resolution succeeded.</summary>
        public string CanonicalPath { get; }

        /// <summary>Gets the operator-readable error for a denied operation.</summary>
        public string Error { get; }

        /// <summary>Gets the failure category for a denied operation.</summary>
        public PathAccessFailure? Failure { get; }

        /// <summary>Creates an allowed decision for a canonical path.</summary>
        public static PathAccessDecision Allow(string canonicalPath)
            => new(true, canonicalPath, string.Empty, null);

        /// <summary>Creates a denied decision with a failure category and optional canonical path.</summary>
        public static PathAccessDecision Deny(
            string error,
            PathAccessFailure failure,
            string canonicalPath = "")
            => new(false, canonicalPath, error, failure);
    }

    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly ToolPathPolicy _protectedPaths;
    private readonly Lazy<IReadOnlyList<string>> _cachedGlobalReadRoots;
    private readonly Lazy<string?> _cachedWorkspacesRoot;
    private readonly IReadOnlyList<string> _sessionRoots;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    // paths is required (not nullable): the workspaces/global-read roots are
    // sourced from it, and a null would silently drop them — the exact silent
    // fallback that let autonomous workspace access break unnoticed (#1493).
    public PathAccessPolicy(
        ToolConfig toolConfig,
        NetclawPaths paths,
        ToolPathPolicy protectedPaths)
    {
        _profileResolver = new ToolAudienceProfileResolver(toolConfig, paths);
        _protectedPaths = protectedPaths;

        // SessionsDirectory contains version-2 envelopes and legacy workspaces.
        // SessionLogsDirectory contains only legacy raw logs. Both roots remain
        // readable so one session can inspect another session's authorized data.
        _sessionRoots = new[]
            {
                paths.SessionsDirectory,
                paths.SessionLogsDirectory
            }
            .Select(PathUtility.Normalize)
            .Distinct(PathComparer)
            .ToArray();
        _cachedGlobalReadRoots = new Lazy<IReadOnlyList<string>>(() =>
            _profileResolver.ResolveGlobalReadRoots()
                .Select(PathUtility.Normalize)
                .Distinct(PathComparer)
                .ToArray());
        _cachedWorkspacesRoot = new Lazy<string?>(() =>
        {
            var workspaces = _profileResolver.ResolveWorkspacesDirectory();
            return string.IsNullOrWhiteSpace(workspaces) ? null : PathUtility.Normalize(workspaces);
        });
    }

    /// <summary>Resolves one path and applies its audience and operation policy.</summary>
    public PathAccessDecision Evaluate(
        string rawPath,
        ToolInvocationContext context,
        FileOperation operation)
    {
        if (!TryResolvePath(
                rawPath,
                context,
                operation,
                out var canonicalPath,
                out var error,
                out var failure))
        {
            return PathAccessDecision.Deny(
                error,
                failure ?? throw new InvalidOperationException("A denied path decision must include a failure."),
                canonicalPath);
        }

        return AllowIfUnprotected(canonicalPath, ToProtectionOperation(operation));
    }

    /// <summary>Applies file protection to one parser-canonical shell path.</summary>
    public PathAccessDecision EvaluateShellPath(
        CanonicalShellPath path,
        ToolInvocationContext context)
    {
        if (ShellPathRules.UsesHostPathStyle(path.PathStyle))
            return Evaluate(path.Value, context, FileOperation.Write);

        // Cross-platform parser tests can supply paths from another host style.
        // Only an explicit interactive All profile has enough authority without
        // a host filesystem relationship check. Bounded profiles fail closed.
        return HasUnrestrictedInteractiveFileAccess(context, FileOperation.Write)
            ? PathAccessDecision.Allow(path.Value)
            : PathAccessDecision.Deny(
                "Error: Path relationship could not be verified on this host.",
                PathAccessFailure.AccessDenied,
                path.Value);
    }

    /// <summary>
    /// Evaluates whether one parser-resolved shell path is inside the bounded
    /// roots eligible for reviewed-safe approval coverage.
    /// </summary>
    /// <remarks>
    /// The caller first checks shell capability, shell command policy, and the
    /// conservative <see cref="FileOperation.Write"/> file-protection decision.
    /// This method adds only the narrower reviewed-safe root requirement. It
    /// grants neither shell nor file authority.
    /// </remarks>
    /// <param name="canonicalPath">The parser-resolved path to evaluate.</param>
    /// <param name="context">The invocation that supplies session and project roots.</param>
    /// <param name="pathStyle">The path syntax reported by the shell parser.</param>
    /// <param name="proposedProjectRoot">
    /// A project root that passed declaration policy but is not active yet.
    /// </param>
    /// <param name="includeRootInLinkCheck">
    /// Whether a link at the trusted root itself makes the relationship unsafe.
    /// </param>
    public PathAccessDecision EvaluateReviewedShellPath(
        string canonicalPath,
        ToolInvocationContext context,
        ShellPathStyle pathStyle,
        string? proposedProjectRoot = null,
        bool includeRootInLinkCheck = true)
    {
        // A reviewed diagnostic can read only session roots and an admitted
        // project root. It cannot inherit the broader global read-root catalog.
        var roots = new List<string>();
        AddSessionRoots(roots, context);
        if (context.Audience != TrustAudience.Public)
        {
            if (!string.IsNullOrWhiteSpace(context.ProjectDirectory))
                roots.Add(context.ProjectDirectory);
            if (!string.IsNullOrWhiteSpace(proposedProjectRoot))
                roots.Add(proposedProjectRoot);
        }

        foreach (var root in roots.Distinct(PathComparer))
        {
            try
            {
                // First, compare paths with the parser-declared shell style.
                // This supports Windows syntax on a non-Windows review host.
                var normalizedPath = string.Empty;
                var normalizedRoot = string.Empty;
                var usesShellPathStyle = ShellPathRules.TryNormalize(canonicalPath, pathStyle, out normalizedPath)
                                         && ShellPathRules.TryNormalize(root, pathStyle, out normalizedRoot);
                if (usesShellPathStyle
                    && !ShellPathRules.IsWithinRoot(normalizedPath, normalizedRoot, pathStyle))
                {
                    continue;
                }

                // A valid foreign-style path can use lexical containment only.
                if (!usesShellPathStyle)
                {
                    // Host-style paths use the platform path API. Both inputs
                    // must be absolute before the policy compares them.
                    if (!Path.IsPathFullyQualified(canonicalPath)
                        || !Path.IsPathFullyQualified(root))
                    {
                        continue;
                    }

                    normalizedPath = PathUtility.Normalize(canonicalPath);
                    normalizedRoot = PathUtility.Normalize(root);
                    if (!PathUtility.IsNormalizedWithinRoot(normalizedPath, normalizedRoot))
                        continue;
                }

                // The host can inspect links only for its own path style.
                // Foreign-style paths stay lexical and fail closed elsewhere.
                if ((!usesShellPathStyle || ShellPathRules.UsesHostPathStyle(pathStyle))
                    && PathUtility.ContainsSymlinkSegment(
                        normalizedRoot,
                        normalizedPath,
                        includeRootInLinkCheck))
                {
                    return PathAccessDecision.Deny(
                        "Error: Path crosses a filesystem link inside a trusted root.",
                        PathAccessFailure.AccessDenied,
                        normalizedPath);
                }

                // Shell protected-path policy ran before this bounded-root check.
                return PathAccessDecision.Allow(normalizedPath);
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                return PathAccessDecision.Deny(
                    "Error: Path relationship could not be verified.",
                    PathAccessFailure.AccessDenied,
                    canonicalPath);
            }
        }

        return PathAccessDecision.Deny(
            "Error: Path is outside trusted roots.",
            PathAccessFailure.AccessDenied,
            canonicalPath);
    }

    /// <summary>Gets the effective trusted roots for one operation and invocation.</summary>
    public IReadOnlyList<string> GetTrustedRoots(ToolInvocationContext context, FileOperation accessKind)
    {
        var profile = _profileResolver.ResolveProfile(context);
        var access = GetAccessProfile(profile, accessKind);
        if (access.Mode == ToolFilesystemMode.None)
            return [];

        if (access.Mode == ToolFilesystemMode.All)
            return ResolveUnattendedTrustedRoots(context, accessKind);

        return ResolveAndMergeRoots(access, context, context.Audience, accessKind);
    }

    private bool TryResolvePath(
        string rawPath,
        ToolInvocationContext context,
        FileOperation accessKind,
        out string fullPath,
        out string error,
        out PathAccessFailure? failure)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Any(char.IsControl))
            {
                fullPath = string.Empty;
                error = "Error: Invalid path.";
                failure = PathAccessFailure.InvalidInput;
                return false;
            }

            if (Path.IsPathFullyQualified(rawPath))
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            else if (Path.IsPathRooted(rawPath))
            {
                fullPath = string.Empty;
                error = "Error: Invalid path: partially qualified paths are not supported.";
                failure = PathAccessFailure.InvalidInput;
                return false;
            }
            else
            {
                var baseResult = TryGetRelativePathBase(
                    context,
                    accessKind,
                    out var baseDirectory);
                if (baseResult == PathBaseStatus.Resolved)
                {
                    fullPath = Path.GetFullPath(rawPath, baseDirectory);
                }
                else
                {
                    fullPath = string.Empty;
                    if (baseResult == PathBaseStatus.Denied)
                    {
                        error = "Error: The project or session directory contains an unsafe filesystem link.";
                        failure = PathAccessFailure.AccessDenied;
                    }
                    else
                    {
                        error = "Error: invalid_context: No project or session directory is available.";
                        failure = PathAccessFailure.MissingBase;
                    }

                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            error = $"Error: Invalid path: {ex.Message}";
            failure = PathAccessFailure.InvalidInput;
            return false;
        }

        var profile = _profileResolver.ResolveProfile(context);
        var access = GetAccessProfile(profile, accessKind);
        var audience = context.Audience;
        var protectionOperation = ToProtectionOperation(accessKind);

        if (access.Mode == ToolFilesystemMode.All)
        {
            // An interactive Mode.All profile grants broad file authority.
            // Approval is a later gate and does not widen this file profile.
            // Unattended runs remain confined to trusted roots because they
            // cannot request new authority from a user.
            // Project-scope declarations opt out of interactive Personal reach.
            // They stay confined to trusted roots even for default
            // Mode.All profiles: its declaration supplies the project directory
            // to reviewed-safe policy and feeds project identity files into the prompt.
            if (accessKind == FileOperation.DeclareProjectScope
                || context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Unavailable)
            {
                var allowed = TryResolveWithinTrustedRoots(fullPath, context, accessKind, out error);
                failure = allowed ? null : PathAccessFailure.AccessDenied;
                return allowed;
            }

            error = string.Empty;
            failure = null;
            return true;
        }

        var label = GetAudienceLabel(audience);

        if (access.Mode == ToolFilesystemMode.None)
        {
            error = $"Error: {label} trust context does not allow {protectionOperation.ToString().ToLowerInvariant()} access to local files.";
            failure = PathAccessFailure.AccessDenied;
            return false;
        }

        var roots = ResolveAndMergeRoots(access, context, audience, accessKind);

        if (roots.Count == 0)
        {
            error = $"Error: {label} trust context does not have any configured local file roots for {protectionOperation.ToString().ToLowerInvariant()} access.";
            failure = PathAccessFailure.AccessDenied;
            return false;
        }

        var relationship = GetHostPathRelationship(fullPath, roots);
        if (relationship == PathRelationship.WithinTrustedRoot)
        {
            error = string.Empty;
            failure = null;
            return true;
        }

        if (relationship == PathRelationship.CrossesLinkBoundary)
        {
            error = $"Error: {label} trust context may not access files through symlinked paths inside the current session directory or configured roots.";
            failure = PathAccessFailure.AccessDenied;
            return false;
        }

        if (relationship == PathRelationship.Unverifiable)
        {
            error = $"Error: {label} trust context could not verify the path relationship to the current session directory or configured roots.";
            failure = PathAccessFailure.AccessDenied;
            return false;
        }

        error = audience == TrustAudience.Public
            ? $"Error: {label} trust context may only access files inside the current session directory."
            : $"Error: {label} trust context may only access files inside the current session directory or configured roots: {string.Join(", ", roots)}.";
        failure = PathAccessFailure.AccessDenied;
        return false;
    }

    private PathBaseStatus TryGetRelativePathBase(
        ToolInvocationContext context,
        FileOperation accessKind,
        out string baseDirectory)
    {
        var projectResult = TryNormalizeAbsoluteBase(
            context.ProjectDirectory,
            requireExistingDirectory: true,
            out baseDirectory);
        if (projectResult == PathBaseStatus.Resolved)
        {
            var relationship = GetPathRelationship(baseDirectory, context, accessKind);
            if (relationship == PathRelationship.WithinTrustedRoot
                || (relationship == PathRelationship.OutsideTrustedRoots
                    && HasUnrestrictedInteractiveFileAccess(context, accessKind)))
            {
                return PathBaseStatus.Resolved;
            }

            baseDirectory = string.Empty;
            return PathBaseStatus.Denied;
        }

        if (projectResult == PathBaseStatus.Denied)
            return PathBaseStatus.Denied;

        return TryNormalizeAbsoluteBase(context.SessionDirectory, requireExistingDirectory: false, out baseDirectory);
    }

    private bool HasUnrestrictedInteractiveFileAccess(
        ToolInvocationContext context,
        FileOperation operation)
        => operation != FileOperation.DeclareProjectScope
           && context.RunScope.InteractiveApproval is InteractiveApprovalCapability.Available
           && GetAccessProfile(_profileResolver.ResolveProfile(context), operation).Mode
           == ToolFilesystemMode.All;

    private PathRelationship GetPathRelationship(
        string projectDirectory,
        ToolInvocationContext context,
        FileOperation accessKind)
    {
        // A project base must derive from authority that existed before the
        // declaration. Do not treat the project path as its own trusted root.
        var roots = new List<string>();
        AddSessionRoots(roots, context);

        var profile = _profileResolver.ResolveProfile(context);
        roots.AddRange(_profileResolver.ResolveRoots(profile.ReadFiles, context));
        roots.AddRange(_cachedGlobalReadRoots.Value);
        if (ToProtectionOperation(accessKind) != FileOperation.Read
            && _cachedWorkspacesRoot.Value is { } workspacesRoot)
        {
            roots.Add(workspacesRoot);
        }

        return GetHostPathRelationship(projectDirectory, roots);
    }

    private static PathBaseStatus TryNormalizeAbsoluteBase(
        string? candidate,
        bool requireExistingDirectory,
        out string baseDirectory)
    {
        baseDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Any(char.IsControl)
            || !Path.IsPathFullyQualified(candidate))
            return PathBaseStatus.Unavailable;

        try
        {
            var normalized = Path.GetFullPath(candidate);
            if (requireExistingDirectory && !Directory.Exists(normalized))
                return PathBaseStatus.Unavailable;
            if (Directory.Exists(normalized)
                && (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            {
                return PathBaseStatus.Denied;
            }

            baseDirectory = normalized;
            return PathBaseStatus.Resolved;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return PathBaseStatus.Denied;
        }
    }

    /// <summary>Describes whether a relative-path base is usable.</summary>
    private enum PathBaseStatus
    {
        /// <summary>No project or session base is available.</summary>
        Unavailable,

        /// <summary>The policy resolved a usable absolute base.</summary>
        Resolved,

        /// <summary>A candidate base exists but fails a safety check.</summary>
        Denied
    }

    /// <summary>Describes one canonical path's relationship to trusted roots.</summary>
    private enum PathRelationship
    {
        /// <summary>The path is not inside a trusted root.</summary>
        OutsideTrustedRoots,

        /// <summary>The path is inside a trusted root without a link escape.</summary>
        WithinTrustedRoot,

        /// <summary>The path uses a filesystem link inside a trusted root.</summary>
        CrossesLinkBoundary,

        /// <summary>The policy cannot prove the path relationship.</summary>
        Unverifiable
    }

    private static ToolFilesystemAccessProfile GetAccessProfile(ToolAudienceProfile profile, FileOperation accessKind) =>
        accessKind switch
        {
            FileOperation.Read => profile.ReadFiles,
            FileOperation.Write => profile.WriteFiles,
            FileOperation.Attach => profile.AttachFiles,
            _ => profile.ReadFiles
        };

    /// <summary>
    /// Resolves profile roots and merges global read roots for read access.
    /// Single source of truth for root resolution — used by both
    /// <see cref="GetTrustedRoots"/> and <see cref="TryResolvePath"/>.
    /// Public audience is excluded from global read roots (skills, identity,
    /// workspaces) — it may only access the shared session trusted roots.
    /// </summary>
    private IReadOnlyList<string> ResolveAndMergeRoots(
        ToolFilesystemAccessProfile access,
        ToolInvocationContext context,
        TrustAudience audience,
        FileOperation accessKind)
    {
        var roots = _profileResolver.ResolveRoots(access, context)
            .Select(PathUtility.Normalize)
            .ToList();

        AddSessionRoots(roots, context);

        if (ToProtectionOperation(accessKind) == FileOperation.Read
            && audience != TrustAudience.Public)
        {
            foreach (var globalRoot in _cachedGlobalReadRoots.Value)
                roots.Add(globalRoot);
        }

        return roots.Distinct(PathComparer).ToArray();
    }

    /// <summary>
    /// Confines an unattended session whose audience would otherwise grant
    /// unrestricted (<see cref="ToolFilesystemMode.All"/>) access to its
    /// trusted roots. Fails closed when no trusted root is available.
    /// </summary>
    private bool TryResolveWithinTrustedRoots(
        string fullPath,
        ToolInvocationContext context,
        FileOperation accessKind,
        out string error)
    {
        var roots = ResolveUnattendedTrustedRoots(context, accessKind);
        if (roots.Count == 0)
        {
            error = "Error: unattended session has no trusted file roots.";
            return false;
        }

        var relationship = GetHostPathRelationship(fullPath, roots);
        if (relationship == PathRelationship.WithinTrustedRoot)
        {
            error = string.Empty;
            return true;
        }

        if (relationship == PathRelationship.CrossesLinkBoundary)
        {
            error = "Error: unattended session may not access files through links inside trusted roots.";
            return false;
        }

        if (relationship == PathRelationship.Unverifiable)
        {
            error = "Error: unattended session could not verify the path relationship to trusted roots.";
            return false;
        }

        error = "Error: unattended session may only access files inside trusted roots.";
        return false;
    }

    private static PathRelationship GetHostPathRelationship(
        string fullPath,
        IEnumerable<string> roots)
    {
        foreach (var candidate in roots)
        {
            try
            {
                var root = Path.GetFullPath(candidate);
                if (!PathUtility.IsWithinRoot(fullPath, root))
                    continue;

                return PathUtility.ContainsSymlinkSegment(root, fullPath, includeRoot: true)
                    ? PathRelationship.CrossesLinkBoundary
                    : PathRelationship.WithinTrustedRoot;
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or PathTooLongException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                return PathRelationship.Unverifiable;
            }
        }

        return PathRelationship.OutsideTrustedRoots;
    }

    /// <summary>
    /// Resolves trusted roots for an unattended invocation from the shared
    /// Netclaw session roots and current project
    /// directory, available for both reads and writes. Read access
    /// additionally includes the non-sensitive global read roots (skills,
    /// identity, workspaces). Write/attach access additionally includes the
    /// configured <em>workspaces</em> directory only — the operator's designated
    /// writable working area — but NOT skills/identity, which are system-managed
    /// (an unattended session must never rewrite its own identity or skills).
    /// Plain file writes are not gated by the interactive approval system, so
    /// confining them to only the current session and project blocked legitimate cross-run state in
    /// the workspace without a security benefit. No additional plumbing — the
    /// cached read roots and workspaces root already exist on this policy.
    /// </summary>
    private IReadOnlyList<string> ResolveUnattendedTrustedRoots(ToolInvocationContext context, FileOperation accessKind)
    {
        var roots = new List<string>();

        AddSessionRoots(roots, context);

        if (!string.IsNullOrWhiteSpace(context.ProjectDirectory))
            roots.Add(context.ProjectDirectory);

        if (ToProtectionOperation(accessKind) == FileOperation.Read)
            roots.AddRange(_cachedGlobalReadRoots.Value);
        else if (_cachedWorkspacesRoot.Value is { } workspacesRoot)
            roots.Add(workspacesRoot);

        return roots
            .Select(PathUtility.Normalize)
            .Distinct(PathComparer)
            .ToArray();
    }

    private void AddSessionRoots(List<string> roots, ToolInvocationContext context)
    {
        roots.AddRange(_sessionRoots);

        if (context.SessionStorage?.Binding is { } binding)
            roots.Add(binding.EnvelopeRoot.Value);

        // Preserve access for legacy and already-running sessions whose bound
        // directory predates the shared session-root layout.
        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(context.SessionDirectory);
    }

    private bool IsProtected(string path, FileOperation operation)
        => operation == FileOperation.Write
            ? _protectedPaths.IsDenied(path)
            : _protectedPaths.IsReadDenied(path);

    private static FileOperation ToProtectionOperation(FileOperation operation)
        => operation == FileOperation.DeclareProjectScope
            ? FileOperation.Read
            : operation;

    private PathAccessDecision AllowIfUnprotected(string canonicalPath, FileOperation operation)
    {
        if (!IsProtected(canonicalPath, operation))
            return PathAccessDecision.Allow(canonicalPath);

        var error = operation == FileOperation.Write
            ? FileToolErrors.ControlPlaneWriteDenied(canonicalPath)
            : FileToolErrors.CredentialReadDenied(canonicalPath);
        return PathAccessDecision.Deny(error, PathAccessFailure.AccessDenied, canonicalPath);
    }

    private static string GetAudienceLabel(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => "Public",
        TrustAudience.Team => "Team",
        TrustAudience.Personal => "Personal",
        _ => "Public"
    };

}
