// -----------------------------------------------------------------------
// <copyright file="PathAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Chooses the path boundaries that a file tool holds, and turns the filesystem
/// authority's answer into the file-tool result.
/// </summary>
/// <remarks>
/// The boundaries come only from the audience profile, the session, and the
/// project. No approval grant enters this list, so a shell grant never authorizes
/// a file tool (R8). Only this class produces <see cref="PathBoundary.Unrestricted"/>
/// (R1). Permission to use a directory does not permit access to protected files
/// inside it. Shell callers must also check shell permissions separately.
/// </remarks>
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

    /// <summary>Returns either permission to use a resolved path or the reason for denial.</summary>
    internal abstract class PathAccessDecision
    {
        private PathAccessDecision() { }

        /// <summary>Returns the canonical path after the caller handles a denied decision.</summary>
        internal string GetAllowedPath() =>
            this switch
            {
                Allowed allowed => allowed.CanonicalPath,
                Denied => throw new InvalidOperationException("A denied path decision has no authorized path."),
                _ => throw new InvalidOperationException("Unexpected path decision.")
            };

        /// <summary>Permits the requested operation on this resolved path.</summary>
        internal sealed class Allowed(string canonicalPath) : PathAccessDecision
        {
            public string CanonicalPath { get; } = canonicalPath;
        }

        /// <summary>Rejects the operation. Its path, if present, is diagnostic data only.</summary>
        internal sealed class Denied(
            string error,
            PathAccessFailure failure,
            string? diagnosticPath) : PathAccessDecision
        {
            public string Error { get; } = error;
            public PathAccessFailure Failure { get; } = failure;
            /// <summary>The rejected path, or null if resolution failed. It is not safe for unrestricted display.</summary>
            public string? DiagnosticPath { get; } = diagnosticPath;
        }

        /// <summary>Records permission after the caller completes the required policy checks.</summary>
        public static PathAccessDecision Allow(string canonicalPath) => new Allowed(canonicalPath);

        /// <summary>Records the failure without granting access to the diagnostic path.</summary>
        public static PathAccessDecision Deny(
            string error,
            PathAccessFailure failure,
            string diagnosticPath = "")
            => new Denied(error, failure, string.IsNullOrEmpty(diagnosticPath) ? null : diagnosticPath);
    }

    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly FileSystemAuthority _fileSystem;
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
        _fileSystem = protectedPaths.FileSystem;

        // SessionsDirectory contains version-2 envelopes and legacy workspaces.
        // SessionLogsDirectory contains only legacy raw logs. Both roots remain
        // available to Personal; restricted audiences receive only their own paths.
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
        if (!TryResolvePath(rawPath, context, operation, out var fullPath, out var error, out var failure))
            return PathAccessDecision.Deny(error, failure, fullPath);

        var protectionOperation = ToProtectionOperation(operation);
        var access = GetAccessProfile(_profileResolver.ResolveProfile(context), operation);
        var label = GetAudienceLabel(context.Audience);
        // An attended and an unattended run get the same file reach (decision
        // D2). Only a project-scope declaration stays inside the trusted roots.
        var confined = access.Mode == ToolFilesystemMode.All
                       && operation == FileOperation.DeclareProjectScope;

        IReadOnlyList<string> roots = [];
        IReadOnlyList<PathBoundary> boundaries;
        var stoppedAtUnusableRoot = false;
        if (access.Mode == ToolFilesystemMode.All && !confined)
        {
            // A Mode.All profile grants broad file authority, attended or not.
            // Approval is a later gate and does not widen this file profile.
            boundaries = [new PathBoundary.Unrestricted()];
        }
        else if (access.Mode == ToolFilesystemMode.None)
        {
            return PathAccessDecision.Deny(
                $"Error: {label} trust context does not allow {ToOperationText(protectionOperation)} access to local files.",
                PathAccessFailure.AccessDenied,
                fullPath);
        }
        else
        {
            // Project-scope declarations opt out of Mode.All reach: the
            // declaration supplies the project directory to reviewed-safe
            // policy and to the prompt.
            roots = confined
                ? ResolveTrustedRoots(context, operation)
                : ResolveAndMergeRoots(access, context, context.Audience, operation);
            if (roots.Count == 0)
            {
                return PathAccessDecision.Deny(
                    confined
                        ? "Error: a project directory has no trusted file roots."
                        : $"Error: {label} trust context does not have any configured local file roots for {ToOperationText(protectionOperation)} access.",
                    PathAccessFailure.AccessDenied,
                    fullPath);
            }

            boundaries = CreateRootBoundaries(roots, fullPath, context, operation, out stoppedAtUnusableRoot);
        }

        if (!CanonicalPath.TryCreateHost(fullPath, relativeBase: null, out var path))
            return DenyFileRelationship(PathDecision.Unverifiable, confined, label, context.Audience, roots, fullPath);

        var decision = _fileSystem.Evaluate(path, ToAuthorityOperation(protectionOperation), boundaries);
        if (stoppedAtUnusableRoot && decision is PathDecision.Outside)
            decision = PathDecision.Unverifiable;

        return decision switch
        {
            PathDecision.Allowed => PathAccessDecision.Allow(fullPath),
            PathDecision.Protected => DenyProtected(fullPath, protectionOperation),
            _ => DenyFileRelationship(decision, confined, label, context.Audience, roots, fullPath)
        };
    }

    /// <summary>Checks a file that a tool creates as part of an already permitted operation.</summary>
    /// <remarks>
    /// The caller supplies the session workspace, or its configured output directory when no session exists.
    /// This check does not require general file-write permission. It still rejects protected paths and filesystem links.
    /// </remarks>
    internal PathAccessDecision EvaluateGeneratedDestination(string path, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return PathAccessDecision.Deny("Error: invalid_context: No output directory available.", PathAccessFailure.InvalidInput);

        if (!CanonicalPath.TryCreateHost(path, relativeBase: null, out var destination)
            || !CanonicalPath.TryCreateHost(outputDirectory, relativeBase: null, out var directory))
        {
            return PathAccessDecision.Deny("Error: Invalid destination path.", PathAccessFailure.InvalidInput);
        }

        // A drive root (or "/") is never an output directory. It would contain every path.
        var decision = directory.IsDriveRoot
            ? PathDecision.Unverifiable
            : _fileSystem.Evaluate(destination, PathOperation.Write, [CreateTrustedFolder(directory)]);
        return decision switch
        {
            PathDecision.Allowed => PathAccessDecision.Allow(destination.Value),
            PathDecision.Protected => DenyProtected(destination.Value, FileOperation.Write),
            _ => PathAccessDecision.Deny(
                "Error: File destination must stay inside its output directory without links.",
                PathAccessFailure.AccessDenied,
                destination.Value)
        };
    }

    /// <summary>Applies file protection to one parser-canonical shell path.</summary>
    public PathAccessDecision EvaluateShellPath(
        CanonicalPath path,
        ToolInvocationContext context)
    {
        if (path.IsHostStyle)
            return Evaluate(path.Value, context, FileOperation.Write);

        // Cross-platform parser tests can supply paths from another host style.
        // Only an explicit All profile has enough authority without a host
        // filesystem relationship check. Bounded profiles fail closed.
        return HasUnrestrictedFileAccess(context, FileOperation.Write)
            ? PathAccessDecision.Allow(path.Value)
            : PathAccessDecision.Deny(
                "Error: Path relationship could not be verified on this host.",
                PathAccessFailure.AccessDenied,
                path.Value);
    }

    /// <summary>
    /// Evaluates whether one parser-resolved shell path is eligible for
    /// reviewed-safe approval coverage.
    /// </summary>
    /// <remarks>
    /// The caller first checks shell capability, shell command policy, and the
    /// conservative <see cref="FileOperation.Write"/> file-protection decision.
    /// The path is eligible when the audience profile lets a file tool read it.
    /// Otherwise it must be inside a session root or the project root (R12).
    /// Attended and unattended runs use the same rule (D2). The method grants
    /// neither shell nor file authority.
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
        // A reviewed diagnostic can read each path that the audience profile
        // lets a file tool read, attended or not (decision D2). The read
        // decision applies protection to the lexical and the link-resolved path.
        if (IsReadableByAudience(canonicalPath, context, pathStyle))
            return PathAccessDecision.Allow(canonicalPath);

        // Otherwise a reviewed diagnostic can read only session roots and an
        // admitted project root. It cannot inherit the global read-root catalog.
        var roots = new List<string>();
        AddSessionRoots(roots, context);
        if (context.Audience != TrustAudience.Public)
        {
            if (!string.IsNullOrWhiteSpace(context.ProjectDirectory))
                roots.Add(context.ProjectDirectory);
            if (!string.IsNullOrWhiteSpace(proposedProjectRoot))
                roots.Add(proposedProjectRoot);
        }

        var links = includeRootInLinkCheck ? LinkRule.IncludingRoot : LinkRule.BelowRoot;
        foreach (var root in roots.Distinct(PathComparer))
        {
            // Compare with the parser-declared shell style first. This supports
            // Windows syntax on a non-Windows review host. Otherwise both values
            // must be absolute host paths.
            CanonicalPath path;
            CanonicalPath rootPath;
            if (!CanonicalPath.TryCreate(canonicalPath, relativeBase: null, pathStyle, out path)
                || !CanonicalPath.TryCreate(root, relativeBase: null, pathStyle, out rootPath))
            {
                if (!Path.IsPathFullyQualified(canonicalPath) || !Path.IsPathFullyQualified(root))
                    continue;

                if (!CanonicalPath.TryCreateHost(canonicalPath, relativeBase: null, out path)
                    || !CanonicalPath.TryCreateHost(root, relativeBase: null, out rootPath))
                {
                    return DenyUnverifiedReviewedPath(canonicalPath);
                }
            }

            // Shell protected-path policy ran before this bounded-root check.
            switch (FileSystemAuthority.EvaluateMembership(path, [new PathBoundary.Folder(rootPath, links)]))
            {
                case PathDecision.Allowed:
                    return PathAccessDecision.Allow(path.Value);
                case PathDecision.CrossesLink:
                    return PathAccessDecision.Deny(
                        "Error: Path crosses a filesystem link inside a trusted root.",
                        PathAccessFailure.AccessDenied,
                        path.Value);
                case PathDecision.Unverifiable:
                    return DenyUnverifiedReviewedPath(canonicalPath);
            }
        }

        return PathAccessDecision.Deny(
            "Error: Path is outside trusted roots.",
            PathAccessFailure.AccessDenied,
            canonicalPath);
    }

    // SECURITY: only a host path of the shell's own style can use file-tool read
    // authority. A path of another style (C:\x on Linux) is not fully qualified
    // here, and Evaluate would read it as a path relative to the project.
    private bool IsReadableByAudience(
        string canonicalPath,
        ToolInvocationContext context,
        ShellPathStyle pathStyle)
        => CanonicalPath.IsHostPathStyle(pathStyle)
           && Path.IsPathFullyQualified(canonicalPath)
           && Evaluate(canonicalPath, context, FileOperation.Read) is PathAccessDecision.Allowed;

    /// <summary>Gets the effective trusted roots for one operation and invocation.</summary>
    public IReadOnlyList<string> GetTrustedRoots(ToolInvocationContext context, FileOperation accessKind)
    {
        var profile = _profileResolver.ResolveProfile(context);
        var access = GetAccessProfile(profile, accessKind);
        if (access.Mode == ToolFilesystemMode.None)
            return [];

        if (access.Mode == ToolFilesystemMode.All)
            return ResolveTrustedRoots(context, accessKind);

        return ResolveAndMergeRoots(access, context, context.Audience, accessKind);
    }

    /// <summary>Resolves the raw text to a host path. It checks no boundary.</summary>
    /// <remarks>File tools never expand <c>~</c>: <c>~/x</c> is a relative literal under the base (R6).</remarks>
    private bool TryResolvePath(
        string rawPath,
        ToolInvocationContext context,
        FileOperation accessKind,
        out string fullPath,
        out string error,
        out PathAccessFailure failure)
    {
        fullPath = string.Empty;
        error = string.Empty;
        failure = PathAccessFailure.InvalidInput;
        try
        {
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Any(char.IsControl))
            {
                error = "Error: Invalid path.";
                return false;
            }

            if (Path.IsPathFullyQualified(rawPath))
            {
                fullPath = Path.GetFullPath(rawPath);
                return true;
            }

            if (Path.IsPathRooted(rawPath))
            {
                error = "Error: Invalid path: partially qualified paths are not supported.";
                return false;
            }

            var baseResult = TryGetRelativePathBase(context, accessKind, out var baseDirectory);
            if (baseResult == PathBaseStatus.Resolved)
            {
                fullPath = Path.GetFullPath(rawPath, baseDirectory);
                return true;
            }

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
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            error = $"Error: Invalid path: {ex.Message}";
            failure = PathAccessFailure.InvalidInput;
            return false;
        }
    }

    private PathBaseStatus TryGetRelativePathBase(
        ToolInvocationContext context,
        FileOperation accessKind,
        out string baseDirectory)
    {
        var projectResult = TryNormalizeAbsoluteBase(
            context.ProjectDirectory,
            requireExistingDirectory: true,
            out baseDirectory,
            out var projectPath);
        if (projectResult == PathBaseStatus.Resolved)
        {
            var boundaries = CreateFolders(GetProjectBaseRoots(context, accessKind), out var stoppedAtUnusableRoot);
            var decision = FileSystemAuthority.EvaluateMembership(projectPath, boundaries);
            if (stoppedAtUnusableRoot && decision is PathDecision.Outside)
                decision = PathDecision.Unverifiable;
            if (decision is PathDecision.Allowed
                || (decision is PathDecision.Outside
                    && HasUnrestrictedFileAccess(context, accessKind)))
            {
                return PathBaseStatus.Resolved;
            }

            baseDirectory = string.Empty;
            return PathBaseStatus.Denied;
        }

        if (projectResult == PathBaseStatus.Denied)
            return PathBaseStatus.Denied;

        return TryNormalizeAbsoluteBase(
            context.SessionDirectory,
            requireExistingDirectory: false,
            out baseDirectory,
            out _);
    }

    private bool HasUnrestrictedFileAccess(
        ToolInvocationContext context,
        FileOperation operation)
        => operation != FileOperation.DeclareProjectScope
           && GetAccessProfile(_profileResolver.ResolveProfile(context), operation).Mode
           == ToolFilesystemMode.All;

    // A project base must derive from authority that existed before the
    // declaration. Do not treat the project path as its own trusted root.
    private List<string> GetProjectBaseRoots(ToolInvocationContext context, FileOperation accessKind)
    {
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

        return roots;
    }

    private static PathBaseStatus TryNormalizeAbsoluteBase(
        string? candidate,
        bool requireExistingDirectory,
        out string baseDirectory,
        out CanonicalPath basePath)
    {
        baseDirectory = string.Empty;
        basePath = default;
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Any(char.IsControl)
            || !Path.IsPathFullyQualified(candidate))
            return PathBaseStatus.Unavailable;

        try
        {
            var normalized = Path.GetFullPath(candidate);
            if (requireExistingDirectory && !Directory.Exists(normalized))
                return PathBaseStatus.Unavailable;
            if (!CanonicalPath.TryCreateHost(normalized, relativeBase: null, out basePath))
                return PathBaseStatus.Denied;

            // A base that is itself a link can move every relative path.
            var baseLinks = FileSystemAuthority.EvaluateMembership(
                basePath,
                [new PathBoundary.Folder(basePath, LinkRule.IncludingRoot)]);
            if (baseLinks is not PathDecision.Allowed)
                return PathBaseStatus.Denied;

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
    /// <see cref="GetTrustedRoots"/> and <see cref="Evaluate"/>.
    /// Public audience is excluded from global read roots (skills, identity,
    /// workspaces). Its implicit roots cover only the current session.
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
    /// Lists the directories a caller can use without a new user approval.
    /// Reads include shared resources such as skills and identity files.
    /// Writes and attachments include the configured workspaces directory instead.
    /// </summary>
    private IReadOnlyList<string> ResolveTrustedRoots(ToolInvocationContext context, FileOperation accessKind)
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
        // These shared directories expose other sessions' files. Only Personal gets that access by default.
        if (context.Audience == TrustAudience.Personal)
            roots.AddRange(_sessionRoots);

        // The newer layout puts a session and its child runs in one directory.
        // Allow that whole directory so the parent and children can access each other's files.
        if (context.SessionStorage?.Binding is { } binding)
            roots.Add(binding.EnvelopeRoot.Value);

        // Preserve access for legacy and already-running sessions whose bound
        // directory predates the shared session-root layout.
        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(context.SessionDirectory);
    }

    /// <summary>Builds the held boundaries for a file path from its trusted roots.</summary>
    private List<PathBoundary> CreateRootBoundaries(
        IReadOnlyList<string> roots,
        string fullPath,
        ToolInvocationContext context,
        FileOperation operation,
        out bool stoppedAtUnusableRoot)
    {
        var folders = CreateFolders(roots, out stoppedAtUnusableRoot);

        // Older sessions keep their logs together outside their individual session
        // directories. Allow this session's log without exposing the other logs
        // beside it (R11). A declaration never uses the log. An unusable root
        // decides before the log, as it did before.
        if (!stoppedAtUnusableRoot
            && operation != FileOperation.DeclareProjectScope
            && context.SessionStorage is { Binding: null } storage
            && PathComparer.Equals(fullPath, storage.LogPath.Value)
            && CanonicalPath.TryCreateHost(storage.LogPath.Value, relativeBase: null, out var log))
        {
            folders.Add(new PathBoundary.ExactFile(log) { LinkAnchor = FindSessionStorageRoot(log) ?? log });
        }

        return folders;
    }

    /// <summary>
    /// Builds one folder for each root, in root order. The first root decides
    /// when it contains the path.
    /// </summary>
    /// <remarks>
    /// An empty root, <c>/</c>, or a drive root such as <c>C:\</c> is unusable and
    /// stops the list. The earlier path API trimmed <c>/</c> to an empty path and
    /// failed there, and it trimmed <c>C:\</c> to <c>C:</c>, the drive's current directory.
    /// A path that no earlier root contains is then unverifiable, which fails closed.
    /// </remarks>
    private List<PathBoundary> CreateFolders(IEnumerable<string> roots, out bool stoppedAtUnusableRoot)
    {
        var folders = new List<PathBoundary>();
        foreach (var root in roots)
        {
            if (!CanonicalPath.TryCreateHost(root, relativeBase: null, out var rootPath) || rootPath.IsDriveRoot)
            {
                stoppedAtUnusableRoot = true;
                return folders;
            }

            folders.Add(CreateTrustedFolder(rootPath));
        }

        stoppedAtUnusableRoot = false;
        return folders;
    }

    // A session path can pass through a link in a parent directory. The link check
    // starts at the enclosing session storage root, without access to its other files.
    private PathBoundary.Folder CreateTrustedFolder(CanonicalPath root)
        => new(root, LinkRule.IncludingRoot) { LinkAnchor = FindSessionStorageRoot(root) ?? root };

    private CanonicalPath? FindSessionStorageRoot(CanonicalPath path)
    {
        foreach (var sessionRoot in _sessionRoots)
        {
            if (CanonicalPath.TryCreateHost(sessionRoot, relativeBase: null, out var storageRoot)
                && storageRoot.Contains(path))
            {
                return storageRoot;
            }
        }

        return null;
    }

    private static PathAccessDecision DenyFileRelationship(
        PathDecision decision,
        bool confined,
        string label,
        TrustAudience audience,
        IReadOnlyList<string> roots,
        string fullPath)
    {
        var error = (decision, confined) switch
        {
            (PathDecision.CrossesLink, true) =>
                "Error: a project directory may not be declared through links inside trusted roots.",
            (PathDecision.Unverifiable, true) =>
                "Error: Netclaw could not verify the project directory relationship to trusted roots.",
            (_, true) => "Error: a project directory must be inside a trusted root.",
            (PathDecision.CrossesLink, false) =>
                $"Error: {label} trust context may not access files through symlinked paths inside the current session directory or configured roots.",
            (PathDecision.Unverifiable, false) =>
                $"Error: {label} trust context could not verify the path relationship to the current session directory or configured roots.",
            _ when audience == TrustAudience.Public =>
                $"Error: {label} trust context may only access files inside the current session directory.",
            _ => $"Error: {label} trust context may only access files inside the current session directory or configured roots: {string.Join(", ", roots)}."
        };
        return new PathAccessDecision.Denied(error, PathAccessFailure.AccessDenied, string.IsNullOrEmpty(fullPath) ? null : fullPath);
    }

    private static PathAccessDecision DenyUnverifiedReviewedPath(string canonicalPath)
        => PathAccessDecision.Deny(
            "Error: Path relationship could not be verified.",
            PathAccessFailure.AccessDenied,
            canonicalPath);

    private static PathAccessDecision DenyProtected(string canonicalPath, FileOperation operation)
    {
        var error = operation == FileOperation.Write
            ? FileToolErrors.ControlPlaneWriteDenied(canonicalPath)
            : FileToolErrors.CredentialReadDenied(canonicalPath);
        return PathAccessDecision.Deny(error, PathAccessFailure.AccessDenied, canonicalPath);
    }

    private static FileOperation ToProtectionOperation(FileOperation operation)
        => operation == FileOperation.DeclareProjectScope
            ? FileOperation.Read
            : operation;

    private static PathOperation ToAuthorityOperation(FileOperation protectionOperation)
        => protectionOperation == FileOperation.Write ? PathOperation.Write : PathOperation.Read;

    private static string ToOperationText(FileOperation protectionOperation)
        => protectionOperation.ToString().ToLowerInvariant();

    private static string GetAudienceLabel(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => "Public",
        TrustAudience.Team => "Team",
        TrustAudience.Personal => "Personal",
        _ => "Public"
    };
}
