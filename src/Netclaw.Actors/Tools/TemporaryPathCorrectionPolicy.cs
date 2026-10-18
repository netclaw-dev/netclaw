// -----------------------------------------------------------------------
// <copyright file="TemporaryPathCorrectionPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Configuration;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Marks a native tool for which managed temporary-directory advice is valid.
/// </summary>
/// <remarks>
/// The marker describes the native operation. It does not interpret shell
/// command syntax or rewrite a tool call.
/// </remarks>
internal interface IManagedTemporaryDirectoryCorrectionTool;

/// <summary>
/// Identifies advice-only calls that explicitly use the shared platform temp root.
/// This policy grants no authority and does not change the submitted call.
/// </summary>
internal sealed class TemporaryPathCorrectionPolicy
{
    private readonly ShellExecutionEnvironment _environment;
    private readonly IPlatformTemporaryPathInspector _pathInspector;
    private readonly IReadOnlyList<PlatformTemporaryRoot> _temporaryRoots;

    internal TemporaryPathCorrectionPolicy(
        ShellExecutionEnvironment environment,
        string platformTemporaryRoot,
        IPlatformTemporaryPathInspector pathInspector)
        : this(environment, platformTemporaryRoot, pathInspector, [])
    {
    }

    internal TemporaryPathCorrectionPolicy(
        ShellExecutionEnvironment environment,
        string platformTemporaryRoot,
        IPlatformTemporaryPathInspector pathInspector,
        IReadOnlyList<string> additionalTemporaryRoots)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformTemporaryRoot);
        ArgumentNullException.ThrowIfNull(pathInspector);
        ArgumentNullException.ThrowIfNull(additionalTemporaryRoots);

        _environment = environment;
        _pathInspector = pathInspector;
        var roots = new List<PlatformTemporaryRoot>();
        AddTemporaryRoot(platformTemporaryRoot, roots);
        foreach (var additionalRoot in additionalTemporaryRoots)
            AddTemporaryRoot(additionalRoot, roots);

        _temporaryRoots = Array.AsReadOnly(roots.ToArray());
        TemporaryRoot = roots.Count > 0 ? roots[0].Resolved.Value : null;
    }

    internal string? TemporaryRoot { get; }

    internal static TemporaryPathCorrectionPolicy Create(ShellExecutionEnvironment environment)
        => new(
            environment,
            Path.GetTempPath(),
            HostPlatformTemporaryPathInspector.Instance,
            environment.PathStyle == ShellPathStyle.Posix ? ["/tmp"] : []);

    internal ToolCorrection.ManagedTemporaryDirectorySuggested? Evaluate(
        ShellCommandAnalysis analysis,
        IReadOnlyList<ApprovalCandidate> candidates,
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context)
    {
        // Advice requires a proved temporary operation or proved temporary output from a project command.
        // The model submits a new call. This method neither changes the source nor grants authority.
        if (!analysis.IsResolved
            || analysis.HasDynamicSyntax
            || !TryGetManagedTemporaryDirectoryForCorrection(context, out var managedTemporaryDirectory))
            return null;

        PlatformTemporaryRoot temporaryRoot;
        if (TryGetExplicitTemporaryRoot(analysis, arguments, out temporaryRoot)
            && AllScopesStayWithinTemporaryRoot(analysis, candidates, temporaryRoot))
        {
            return new ToolCorrection.ManagedTemporaryDirectorySuggested(
                new ManagedTemporaryCorrectionTarget(managedTemporaryDirectory, temporaryRoot.Resolved.Value));
        }

        if (!TryGetProjectOutputTemporaryRoot(analysis, context, out temporaryRoot))
            return null;

        return new ToolCorrection.ManagedTemporaryDirectorySuggested(
            new ManagedTemporaryCorrectionTarget(
                managedTemporaryDirectory,
                temporaryRoot.Resolved.Value));
    }

    /// <summary>
    /// Suggests the session's managed temporary directory when an interactive
    /// Personal <c>file_write</c> or <c>file_edit</c> call targets an absolute,
    /// unprotected path below a platform temporary root.
    /// </summary>
    /// <remarks>
    /// The tool access policy calls this method after structured path
    /// authorization while it builds an approval-required result. The returned
    /// correction is advice only: it does not rewrite the submitted path or grant
    /// access to either the submitted path or the suggested directory.
    /// </remarks>
    /// <example>
    /// An interactive Personal call such as
    /// <c>file_write(Path: "/tmp/report.md")</c> returns a correction that points
    /// to the run's managed temporary directory. A <c>file_read</c> call, a
    /// relative path such as <c>report.md</c>, a protected path, or the same call
    /// from a Team or Public context returns <see langword="null"/>.
    /// </example>
    internal ToolCorrection.ManagedTemporaryDirectorySuggested? EvaluateStructuredFileChange(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        ToolPathPolicy pathPolicy)
    {
        if (toolName.Value is not (FileWriteTool.ToolName or FileEditTool.ToolName)
            || !TryGetManagedTemporaryDirectoryForCorrection(context, out var managedTemporaryDirectory))
        {
            return null;
        }

        var path = ToolArgumentHelper.GetString(arguments, "Path")
            ?? ToolArgumentHelper.GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || pathPolicy.FileSystem.IsProtected(path, PathOperation.Write)
            || IsInsideOwnManagedTemporaryDirectory(path, managedTemporaryDirectory)
            || !TryGetEligibleTemporaryRoot(path, out var temporaryRoot))
        {
            return null;
        }

        return new ToolCorrection.ManagedTemporaryDirectorySuggested(
            new ManagedTemporaryCorrectionTarget(
                managedTemporaryDirectory,
                temporaryRoot));
    }

    // On POSIX the managed temporary folder is itself below /tmp (issue #2554). Advice that
    // sends the agent from its own folder to the same folder would be noise. Only the folder of
    // this call is exempt: the folder of another session is a different path and still gets advice.
    // A shell call needs no such check. Its advice starts only when the working directory is the
    // platform temporary root itself, and the own folder is a child of that root.
    private bool IsInsideOwnManagedTemporaryDirectory(string path, string managedTemporaryDirectory)
        => TryNormalizePath(path, out var target)
           && TryNormalizePath(managedTemporaryDirectory, out var own)
           && own.Contains(target);

    internal bool IsPlatformTemporaryRoot(string? path)
        => TryGetTemporaryRoot(path, out _);

    /// <summary>
    /// Gets the canonical platform temporary root when the path stays below it without a link escape.
    /// </summary>
    private bool TryGetEligibleTemporaryRoot(string path, out string temporaryRoot)
    {
        if (TryNormalizePath(path, out var normalized))
        {
            foreach (var root in _temporaryRoots)
            {
                if (IsLinkFreeTemporaryPath(normalized, root))
                {
                    temporaryRoot = root.Resolved.Value;
                    return true;
                }
            }
        }

        temporaryRoot = string.Empty;
        return false;
    }

    private bool TryGetProjectOutputTemporaryRoot(
        ShellCommandAnalysis analysis,
        ToolInvocationContext context,
        out PlatformTemporaryRoot temporaryRoot)
    {
        temporaryRoot = default;
        if (_environment.Grammar != ShellGrammar.Bash
            || context.ProjectDirectory is not { } projectDirectory
            || !CanonicalPath.TryCreate(projectDirectory, relativeBase: null, _environment.PathStyle, out var project))
            return false;

        foreach (var root in _temporaryRoots)
        {
            var hasOutput = false;
            var eligible = true;
            foreach (var command in analysis.Commands)
            {
                if ((analysis.RunsNoProgram(command) && command.Redirects.Count > 0)
                    || command.WorkingDirectory is not ShellValueDomain.Exact directory
                    || !CanonicalPath.TryCreate(directory.Value, relativeBase: null, _environment.PathStyle, out var cwd)
                    || FileSystemAuthority.EvaluateMembership(cwd,
                        [new PathBoundary.Folder(project, LinkRule.BelowRoot)]) != PathDecision.Allowed)
                {
                    eligible = false;
                    break;
                }

                foreach (var redirect in command.Redirects)
                {
                    if (redirect is DescriptorDuplicateRedirectAnalysis or DescriptorMoveRedirectAnalysis
                        or DescriptorCloseRedirectAnalysis)
                        continue;
                    if (redirect is not FileRedirectAnalysis { IsComplete: true, Target: ShellValueDomain.Exact exact } file
                        || !ShellRedirectPolicyFacts.IsFileWritingMode(file.Mode)
                        || !CanonicalPath.TryCreate(exact.Value, relativeBase: null, _environment.PathStyle, out var target))
                    {
                        eligible = false;
                        break;
                    }
                    if (ShellRedirectPolicyFacts.IsNullDevice(target))
                        continue;
                    // A project file keeps its ordinary consent, even when the project itself lies under /tmp.
                    if (project.Contains(target))
                    {
                        eligible = false;
                        break;
                    }
                    if (context.SessionStorage is { } storage
                        && CanonicalPath.TryCreate(storage.ManagedTemporary.Directory.Value,
                            relativeBase: null, _environment.PathStyle, out var managed)
                        && FileSystemAuthority.EvaluateMembership(target,
                            [new PathBoundary.Folder(managed, LinkRule.BelowRoot)]) == PathDecision.Allowed)
                        continue;
                    if (!IsLinkFreeTemporaryPath(target, root))
                    {
                        eligible = false;
                        break;
                    }
                    hasOutput = true;
                }
            }

            if (eligible && hasOutput)
            {
                temporaryRoot = root;
                return true;
            }
        }

        return false;
    }

    private bool TryGetExplicitTemporaryRoot(
        ShellCommandAnalysis analysis,
        IDictionary<string, object?>? arguments,
        out PlatformTemporaryRoot temporaryRoot)
    {
        var explicitDirectory = ToolArgumentHelper.GetString(arguments, "WorkingDirectory");
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return TryGetTemporaryRoot(explicitDirectory, out temporaryRoot);

        if (_environment.Grammar != ShellGrammar.Bash)
        {
            temporaryRoot = default;
            return false;
        }

        foreach (var command in analysis.Commands)
        {
            if (command.WorkingDirectoryEffect is
                    ShellWorkingDirectoryEffect.ChangesOnSuccess
                {
                    Target: ShellValueDomain.Exact exact
                }
                && TryGetTemporaryRoot(exact.Value, out temporaryRoot))
            {
                return true;
            }
        }

        temporaryRoot = default;
        return false;
    }

    private bool AllScopesStayWithinTemporaryRoot(
        ShellCommandAnalysis analysis,
        IReadOnlyList<ApprovalCandidate> candidates,
        PlatformTemporaryRoot temporaryRoot)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Directory is null)
                continue;

            if (!IsLinkFreeTemporaryPath(candidate.Directory, temporaryRoot))
                return false;
        }

        foreach (var command in analysis.Commands)
        {
            if (!HasLinkFreeDirectoryTransitionEffect(command, temporaryRoot)
                && (command.WorkingDirectory is not ShellValueDomain.Exact workingDirectory
                    || !IsLinkFreeTemporaryPath(workingDirectory.Value, temporaryRoot)))
            {
                return false;
            }

            foreach (var argument in command.Clause.Args)
            {
                if (!argument.IsPath && !argument.IsCwdAttribution)
                    continue;

                if (string.IsNullOrWhiteSpace(argument.Resolved)
                    || !IsLinkFreeTemporaryPath(argument.Resolved, temporaryRoot))
                {
                    return false;
                }
            }

            foreach (var redirect in command.Redirects)
            {
                var eligible = redirect switch
                {
                    FileRedirectAnalysis file =>
                        HasLinkFreeRedirectTarget(file.Target, temporaryRoot),
                    DescriptorDuplicateRedirectAnalysis => true,
                    DescriptorMoveRedirectAnalysis => true,
                    DescriptorCloseRedirectAnalysis => true,
                    HereDocumentRedirectAnalysis => true,
                    HereStringRedirectAnalysis => true,
                    _ => false
                };
                if (!eligible)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool HasLinkFreeDirectoryTransitionEffect(
        CommandOccurrence command,
        PlatformTemporaryRoot temporaryRoot)
        => _environment.Grammar == ShellGrammar.Bash
           && command.WorkingDirectoryEffect is
               ShellWorkingDirectoryEffect.ChangesOnSuccess
           {
               Target: ShellValueDomain.Exact exact
           }
           && IsLinkFreeTemporaryPath(exact.Value, temporaryRoot);

    private bool HasLinkFreeRedirectTarget(
        ShellValueDomain target,
        PlatformTemporaryRoot temporaryRoot)
        => target switch
        {
            ShellValueDomain.Exact exact =>
                IsLinkFreeTemporaryPath(exact.Value, temporaryRoot),
            ShellValueDomain.FiniteSet finite =>
                finite.Values.Count > 0
                && finite.Values.All(path => IsLinkFreeTemporaryPath(path, temporaryRoot)),
            ShellValueDomain.PathPattern pattern =>
                IsLinkFreeTemporaryPath(pattern.CoveringDirectory, temporaryRoot),
            _ => false
        };

    private bool IsLinkFreeTemporaryPath(
        string path,
        PlatformTemporaryRoot temporaryRoot)
        => TryNormalizePath(path, out var normalized)
           && IsLinkFreeTemporaryPath(normalized, temporaryRoot);

    private bool IsLinkFreeTemporaryPath(
        CanonicalPath path,
        PlatformTemporaryRoot temporaryRoot)
        => temporaryRoot.TryMapToResolved(path, out var resolved)
           && _pathInspector.HasNoLinkEscape(
               temporaryRoot.Resolved.Value,
               resolved.Value,
               _environment.PathStyle);

    private void AddTemporaryRoot(
        string path,
        ICollection<PlatformTemporaryRoot> roots)
    {
        if (!TryNormalizePath(path, out var authored)
            || !_pathInspector.TryResolveRoot(
                path,
                _environment.PathStyle,
                out var resolvedText)
            || !TryNormalizePath(resolvedText, out var resolved)
            || roots.Any(root => root.Authored.IsSamePath(authored)))
        {
            return;
        }

        roots.Add(new PlatformTemporaryRoot(authored, resolved));
    }

    private bool TryGetTemporaryRoot(
        string? path,
        out PlatformTemporaryRoot temporaryRoot)
    {
        temporaryRoot = default;
        if (!TryNormalizePath(path, out var normalized))
            return false;

        foreach (var root in _temporaryRoots)
        {
            if (normalized.IsSamePath(root.Authored)
                || normalized.IsSamePath(root.Resolved))
            {
                temporaryRoot = root;
                return true;
            }
        }

        return false;
    }

    private bool TryGetManagedTemporaryDirectoryForCorrection(
        ToolInvocationContext context,
        out string managedTemporaryDirectory)
    {
        managedTemporaryDirectory = string.Empty;
        if (context.Audience != TrustAudience.Personal
            || !TryNormalizePath(
                context.SessionStorage?.ManagedTemporary.Directory.Value,
                out var normalized)
            || !_pathInspector.SupportsPathInspection(_environment.PathStyle))
        {
            return false;
        }

        managedTemporaryDirectory = normalized.Value;
        return true;
    }

    private bool TryNormalizePath(string? path, out CanonicalPath normalized)
        => CanonicalPath.TryCreate(path, relativeBase: null, _environment.PathStyle, out normalized);
}

/// <summary>
/// Resolves platform temporary roots and verifies host path relationships without a link escape.
/// </summary>
internal interface IPlatformTemporaryPathInspector
{
    bool TryResolveRoot(string path, ShellPathStyle pathStyle, out string resolvedRoot);

    bool HasNoLinkEscape(string root, string path, ShellPathStyle pathStyle);

    bool SupportsPathInspection(ShellPathStyle pathStyle);
}

/// <summary>Uses the filesystem authority to inspect platform temporary paths on the host.</summary>
internal sealed class HostPlatformTemporaryPathInspector : IPlatformTemporaryPathInspector
{
    internal static HostPlatformTemporaryPathInspector Instance { get; } = new();

    private HostPlatformTemporaryPathInspector()
    {
    }

    public bool TryResolveRoot(string path, ShellPathStyle pathStyle, out string resolvedRoot)
    {
        var resolved = PlatformTemporaryRoot.TryResolve(path, pathStyle, out var root);
        resolvedRoot = resolved ? root.Resolved.Value : string.Empty;
        return resolved;
    }

    public bool HasNoLinkEscape(string root, string path, ShellPathStyle pathStyle)
        => CanonicalPath.IsHostPathStyle(pathStyle)
           && !FileSystemAuthority.CrossesLink(root, path, includeAnchor: false);

    public bool SupportsPathInspection(ShellPathStyle pathStyle)
        => CanonicalPath.IsHostPathStyle(pathStyle);
}
