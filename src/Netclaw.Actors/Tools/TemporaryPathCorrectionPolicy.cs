// -----------------------------------------------------------------------
// <copyright file="TemporaryPathCorrectionPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Configuration;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

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
        TemporaryRoot = roots.Count > 0 ? roots[0].Canonical : null;
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
                temporaryRoot.Canonical));
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
            || pathPolicy.IsDenied(path)
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

    internal bool IsEligiblePlatformTemporaryPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && TryGetEligibleTemporaryRoot(path, out _);

    /// <summary>
    /// Gets the canonical platform temporary root when the path stays below it without a link escape.
    /// </summary>
    private bool TryGetEligibleTemporaryRoot(string path, out string temporaryRoot)
    {
        if (TryNormalizePath(path, out var normalized))
        {
            foreach (var root in _temporaryRoots)
            {
                if ((IsWithinRoot(normalized, root.Authored)
                     || IsWithinRoot(normalized, root.Canonical))
                    && IsLinkFreeTemporaryPath(normalized, root))
                {
                    temporaryRoot = root.Canonical;
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
    {
        if (!TryNormalizePath(path, out var normalized)
            || !TryMapToCanonicalTemporaryPath(
                normalized,
                temporaryRoot,
                out var canonicalPath))
        {
            return false;
        }

        return _pathInspector.HasNoLinkEscape(
            temporaryRoot.Canonical,
            canonicalPath,
            _environment.PathStyle);
    }

    private bool TryMapToCanonicalTemporaryPath(
        string path,
        PlatformTemporaryRoot temporaryRoot,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (IsWithinRoot(path, temporaryRoot.Canonical))
        {
            canonicalPath = path;
            return true;
        }

        if (!IsWithinRoot(path, temporaryRoot.Authored))
        {
            return false;
        }

        var relative = path[temporaryRoot.Authored.Length..]
            .TrimStart('/', '\\');
        canonicalPath = relative.Length == 0
            ? temporaryRoot.Canonical
            : temporaryRoot.Canonical.TrimEnd('/', '\\') +
              (_environment.PathStyle == ShellPathStyle.Windows ? '\\' : '/') +
              relative;
        return true;
    }

    private void AddTemporaryRoot(
        string path,
        ICollection<PlatformTemporaryRoot> roots)
    {
        if (!ShellPathRules.TryNormalize(path, _environment.PathStyle, out var authored)
            || !_pathInspector.TryResolveRoot(
                path,
                _environment.PathStyle,
                out var canonical)
            || roots.Any(root => PathEquals(root.Authored, authored)))
        {
            return;
        }

        roots.Add(new PlatformTemporaryRoot(authored, canonical));
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
            if (PathEquals(normalized, root.Authored)
                || PathEquals(normalized, root.Canonical))
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

        managedTemporaryDirectory = normalized;
        return true;
    }

    private bool TryNormalizePath(string? path, out string normalized)
        => ShellPathRules.TryNormalize(path, _environment.PathStyle, out normalized);

    private bool IsWithinRoot(string candidate, string root)
        => ShellPathRules.IsWithinRoot(candidate, root, _environment.PathStyle);

    private bool PathEquals(string left, string right)
        => ShellPathRules.Equals(left, right, _environment.PathStyle);

    private readonly record struct PlatformTemporaryRoot(
        string Authored,
        string Canonical);
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

/// <summary>Uses host filesystem metadata to inspect platform temporary paths.</summary>
internal sealed class HostPlatformTemporaryPathInspector : IPlatformTemporaryPathInspector
{
    internal static HostPlatformTemporaryPathInspector Instance { get; } = new();

    private HostPlatformTemporaryPathInspector()
    {
    }

    public bool TryResolveRoot(string path, ShellPathStyle pathStyle, out string resolvedRoot)
    {
        resolvedRoot = string.Empty;
        if (!ShellPathRules.UsesHostPathStyle(pathStyle)
            || !ShellPathRules.TryNormalize(path, pathStyle, out var normalized)
            || !Directory.Exists(normalized))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(normalized);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return false;

            var current = root;
            var remainder = fullPath.Length > root.Length
                ? fullPath[root.Length..]
                : string.Empty;
            var segments = remainder.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                    return false;

                var target = new DirectoryInfo(current)
                    .ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    current = target.FullName;
            }

            return ShellPathRules.TryNormalize(current, pathStyle, out resolvedRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool HasNoLinkEscape(string root, string path, ShellPathStyle pathStyle)
        => ShellPathRules.UsesHostPathStyle(pathStyle)
           && !PathUtility.ContainsSymlinkSegment(root, path);

    public bool SupportsPathInspection(ShellPathStyle pathStyle)
        => ShellPathRules.UsesHostPathStyle(pathStyle);
}
