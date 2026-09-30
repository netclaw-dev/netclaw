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
        // Suggest a different directory only when the parser proves that the
        // complete shell operation is static and every affected scope stays
        // below one explicit platform temporary root. If any proof is missing,
        // preserve the ordinary approval path: a correction could otherwise
        // change the command's meaning or hide an additional path operation.
        if (!analysis.IsResolved
            || analysis.HasDynamicSyntax
            || !TryGetManagedTemporaryDirectoryForCorrection(context, out var managedTemporaryDirectory)
            || !TryGetExplicitTemporaryRoot(analysis, arguments, out var temporaryRoot)
            || !AllScopesStayWithinTemporaryRoot(analysis, candidates, temporaryRoot))
        {
            return null;
        }

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
            || !TryGetEligibleTemporaryRoot(path, out var temporaryRoot))
        {
            return null;
        }

        return new ToolCorrection.ManagedTemporaryDirectorySuggested(
            new ManagedTemporaryCorrectionTarget(
                managedTemporaryDirectory,
                temporaryRoot));
    }

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
        if (context.RunScope.InteractiveApproval is not InteractiveApprovalCapability.Available
            || context.Audience != TrustAudience.Personal
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
