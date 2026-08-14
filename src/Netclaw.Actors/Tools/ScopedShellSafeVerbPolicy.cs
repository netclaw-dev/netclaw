// -----------------------------------------------------------------------
// <copyright file="ScopedShellSafeVerbPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Layer 1.5 of the shell approval pipeline (between the hard-deny list and
/// the interactive approval gate). The policy covers a reviewed diagnostic
/// only when all parser-owned source and scope guards pass.
///
/// Mirrors <see cref="ScopedFileAccessPolicy"/> for the audience model and
/// the symlink-segment guard. Personal and Team audiences get
/// <c>session_dir + project_dir</c> as their safe-space roots; Public gets
/// <c>session_dir</c> only — Public sessions cannot expand their safe space
/// via <c>set_working_directory</c>, mirroring the read-roots restriction
/// <see cref="ScopedFileAccessPolicy"/> enforces for file_read.
///
/// The policy never relaxes the hard-deny list (layer 1) — that runs first
/// in <see cref="ToolAccessPolicy"/>. It only relaxes the interactive
/// approval gate (layer 2) for phrases that the bundled catalog reviews.
/// </summary>
internal sealed class ScopedShellSafeVerbPolicy
{
    private readonly SafeVerbList _safeVerbs;

    public ScopedShellSafeVerbPolicy(SafeVerbList safeVerbs)
    {
        _safeVerbs = safeVerbs;
    }

    /// <summary>
    /// Returns true when each candidate has a safe verb and a safe effective
    /// directory. The candidate directory takes precedence over the cwd.
    /// </summary>
    public bool AllShortCircuit(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        ToolInvocationContext context)
    {
        if (candidates.Count == 0)
            return false;

        return candidates.All(candidate => ShortCircuits(
            candidate,
            candidate.SourceOccurrence,
            cwd,
            context));
    }

    /// <summary>
    /// Returns true when declaring <paramref name="cwd"/> as the project root
    /// would make every candidate eligible for the reviewed-safe short circuit.
    /// </summary>
    /// <remarks>
    /// This does not grant authority. It identifies a self-correction that the
    /// agent can make through <c>set_working_directory</c>. Every candidate must
    /// already use a reviewed phrase, and every effective directory must remain
    /// beneath the exact cwd supplied for this shell invocation.
    /// </remarks>
    public bool CanShortCircuitAfterProjectDeclaration(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        ToolInvocationContext context)
    {
        if (context.Audience == TrustAudience.Public
            || candidates.Count == 0
            || string.IsNullOrWhiteSpace(cwd))
        {
            return false;
        }

        string fullCwd;
        try
        {
            fullCwd = Path.GetFullPath(cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (ResolveSafeSpaceRoots(context)
            .Any(root => PathUtility.IsWithinRoot(fullCwd, root)))
        {
            return false;
        }

        var prospectiveRoots = ResolveSafeSpaceRoots(context)
            .Append(fullCwd)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var resolvedPaths = ResolveCompatibilityPaths(
                candidate,
                candidate.SourceOccurrence,
                fullCwd);
            if (!IsReviewedDiagnostic(
                    candidate,
                    candidate.SourceOccurrence,
                    prospectiveRoots,
                    resolvedPaths))
            {
                return false;
            }

            var effectiveDirectory = candidate.Directory ?? fullCwd;
            if (string.IsNullOrWhiteSpace(effectiveDirectory))
                return false;

            string fullDirectory;
            try
            {
                fullDirectory = Path.GetFullPath(effectiveDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (!PathUtility.IsWithinRoot(fullDirectory, fullCwd)
                || PathUtility.ContainsSymlinkSegment(fullCwd, fullDirectory))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsReviewedDiagnostic(
        ApprovalCandidate candidate,
        CommandOccurrence? sourceOccurrence,
        IReadOnlyList<string> safeRoots,
        ShellPolicyResolvedPathView? resolvedPaths)
    {
        if (candidate is not
            {
                Shell: { } shell,
                VerbTokens: { }
            }
            || sourceOccurrence is null
            || HasFileWritingRedirect(resolvedPaths)
            || HasUnprovedNonFileSystemSemantics(resolvedPaths)
            || !_safeVerbs.TryMatchReviewedDiagnostic(
                shell,
                candidate.VerbTokens,
                out var matchedTokenCount))
        {
            return false;
        }

        if (sourceOccurrence.Arguments.Any(argument =>
                argument.Element.PrecedingVerbElementCount < matchedTokenCount))
        {
            return false;
        }

        return AllAuthoredPathsStayWithinRoots(
            resolvedPaths,
            shell,
            safeRoots);
    }

    internal bool ShortCircuitsCausalIntent(
        ApprovalCandidate candidate,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
    {
        if (context.Audience != TrustAudience.Personal
            || candidate is not
            {
                Shell: ApprovalShell.Bash,
                VerbTokens: { }
            }
            || pathFacts.IntentScope is not
            {
                State: ShellPolicyPathResolutionState.Known,
                Path: { } intentPath
            }
            || !IsSafePath(
                intentPath.Value,
                intentPath.Value,
                ShellPathStyle.Posix)
            || !IsReviewedDiagnostic(
                candidate,
                pathFacts.SourceOccurrence,
                [intentPath.Value],
                pathFacts.Intent))
        {
            return false;
        }

        return AllEffectivePathsStayWithinIntent(
            pathFacts.Intent,
            intentPath.Value);
    }

    internal bool ShortCircuits(
        ApprovalCandidate candidate,
        CommandOccurrence? sourceOccurrence,
        string? cwd,
        ToolInvocationContext context)
    {
        var safeRoots = ResolveSafeSpaceRoots(context);
        var resolvedPaths = ResolveCompatibilityPaths(candidate, sourceOccurrence);
        if (safeRoots.Count == 0
            || !IsReviewedDiagnostic(candidate, sourceOccurrence, safeRoots, resolvedPaths))
        {
            return false;
        }

        var effectiveDirectory = candidate.Directory ?? cwd;
        if (string.IsNullOrWhiteSpace(effectiveDirectory))
            return false;

        try
        {
            var fullDirectory = Path.GetFullPath(effectiveDirectory);
            return safeRoots.Any(root => IsSafePath(fullDirectory, root));
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }
    }

    internal bool ShortCircuits(
        ApprovalCandidate candidate,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
    {
        var safeRoots = ResolveDeclaredSafeSpaceRoots(context);
        if (safeRoots.Count == 0
            || !IsReviewedDiagnostic(
                candidate,
                pathFacts.SourceOccurrence,
                safeRoots,
                pathFacts.Real)
            || pathFacts.RealScope is not
            {
                State: ShellPolicyPathResolutionState.Known,
                Path: { } realPath
            } realScope)
        {
            return false;
        }

        var pathStyle = candidate.Shell == ApprovalShell.Bash
            ? ShellPathStyle.Posix
            : ShellPathStyle.Windows;
        if (safeRoots.All(root =>
                ShellPathRules.TryNormalize(root, pathStyle, out _)))
        {
            return safeRoots.Any(root => IsSafePath(
                realPath.Value,
                root,
                pathStyle));
        }

        if (string.IsNullOrWhiteSpace(realScope.AuthoredValue))
            return false;

        try
        {
            var fullDirectory = Path.GetFullPath(realScope.AuthoredValue);
            return safeRoots.Any(root => IsSafePath(fullDirectory, root));
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasFileWritingRedirect(ShellPolicyResolvedPathView? resolvedPaths)
        => resolvedPaths?.Facts.Any(static fact =>
            fact.Source is
            {
                Origin: ShellPolicyPathOrigin.Redirect,
                RedirectMode: { } mode
            }
            && ShellRedirectPolicyFacts.IsFileWritingMode(mode)) == true;

    private static bool HasUnprovedNonFileSystemSemantics(
        ShellPolicyResolvedPathView? resolvedPaths)
        => resolvedPaths?.HasUnprovedNonFileSystemSemantics == true;

    private static bool AllAuthoredPathsStayWithinRoots(
        ShellPolicyResolvedPathView? resolvedPaths,
        ApprovalShell shell,
        IReadOnlyList<string> safeRoots)
    {
        if (resolvedPaths is null)
            return false;

        var pathStyle = shell == ApprovalShell.Bash
            ? ShellPathStyle.Posix
            : ShellPathStyle.Windows;
        foreach (var fact in resolvedPaths.Facts.Where(static fact =>
                     fact.Source.Origin == ShellPolicyPathOrigin.AuthoredArgument))
        {
            if (fact.Source.AuthoredPathShape == ShellPathShape.Posix
                    && pathStyle != ShellPathStyle.Posix
                || fact.Source.AuthoredPathShape == ShellPathShape.Windows
                    && pathStyle != ShellPathStyle.Windows
                || fact.Source.DomainKind is not
                    (ShellPolicyPathDomainKind.Exact or ShellPolicyPathDomainKind.FiniteSet)
                || fact.State != ShellPolicyPathResolutionState.Known
                || fact.Paths.Count == 0
                || fact.Paths.Any(path =>
                    !safeRoots.Any(root => IsSafePath(
                        path.Value,
                        root,
                        path.PathStyle))))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllEffectivePathsStayWithinIntent(
        ShellPolicyResolvedPathView? resolvedPaths,
        string intentDirectory)
    {
        if (resolvedPaths is null)
            return false;

        foreach (var fact in resolvedPaths.Facts.Where(static fact =>
                     fact.Source.Origin == ShellPolicyPathOrigin.EffectiveArgument))
        {
            if (fact.Source.DomainKind is not
                    (ShellPolicyPathDomainKind.Exact or ShellPolicyPathDomainKind.FiniteSet)
                || fact.State != ShellPolicyPathResolutionState.Known
                || fact.Paths.Count == 0
                || fact.Paths.Any(path => !IsSafePath(
                    path.Value,
                    intentDirectory,
                    ShellPathStyle.Posix)))
            {
                return false;
            }
        }

        foreach (var fact in resolvedPaths.Facts.Where(static fact =>
                     fact.Source.Origin == ShellPolicyPathOrigin.Redirect))
        {
            if (fact.Source.RedirectMode != FileRedirectMode.Input
                || fact.Source.DomainKind != ShellPolicyPathDomainKind.Exact
                || fact.State != ShellPolicyPathResolutionState.Known
                || fact.Paths.Count != 1
                || !IsSafePath(
                    fact.Paths[0].Value,
                    intentDirectory,
                    ShellPathStyle.Posix))
            {
                return false;
            }
        }

        return true;
    }

    private static ShellPolicyResolvedPathView? ResolveCompatibilityPaths(
        ApprovalCandidate candidate,
        CommandOccurrence? occurrence,
        string? workingDirectoryOverride = null)
    {
        if (candidate.Shell is not { } shell || occurrence is null)
            return null;

        var workingDirectory = workingDirectoryOverride
            ?? (occurrence.WorkingDirectory is ShellValueDomain.Exact exact
                ? exact.Value
                : null);
        var pathStyle = OperatingSystem.IsWindows()
            ? ShellPathStyle.Windows
            : ShellPathStyle.Posix;
        return ShellPolicyOccurrencePathFacts.Create(occurrence)
            .Resolve(workingDirectory, pathStyle, shell);
    }

    private static bool IsSafePath(string path, string root)
    {
        try
        {
            return PathUtility.IsWithinRoot(path, root)
                   && !PathUtility.ContainsSymlinkSegment(root, path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or NotSupportedException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsSafePath(
        string path,
        string root,
        ShellPathStyle pathStyle)
    {
        try
        {
            return ShellPathRules.TryNormalize(path, pathStyle, out var normalizedPath)
                   && ShellPathRules.TryNormalize(root, pathStyle, out var normalizedRoot)
                   && ShellPathRules.IsWithinRoot(
                       normalizedPath,
                       normalizedRoot,
                       pathStyle)
                   && (!ShellPathRules.UsesHostPathStyle(pathStyle)
                       || !PathUtility.ContainsSymlinkSegment(
                           normalizedRoot,
                           normalizedPath));
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or NotSupportedException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the audience-aware safe-space roots for the current
    /// invocation. Personal and Team get <c>session_dir + project_dir</c>;
    /// Public gets <c>session_dir</c> only.
    /// </summary>
    private static IReadOnlyList<string> ResolveSafeSpaceRoots(ToolInvocationContext context)
        => ResolveSafeSpaceRoots(context, static path => PathUtility.Normalize(path));

    private static IReadOnlyList<string> ResolveDeclaredSafeSpaceRoots(
        ToolInvocationContext context)
        => ResolveSafeSpaceRoots(context, static path => path);

    private static IReadOnlyList<string> ResolveSafeSpaceRoots(
        ToolInvocationContext context,
        Func<string, string> mapPath)
    {
        var roots = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(mapPath(context.SessionDirectory));

        // Public audience cannot expand its safe space via project_dir —
        // mirrors the file_read read-roots restriction enforced by
        // ScopedFileAccessPolicy. Even a Public session that has somehow
        // populated WorkingContext.ProjectDirectory does not get to use it
        // as a shell safe-space root.
        if (context.Audience != TrustAudience.Public
            && !string.IsNullOrWhiteSpace(context.ProjectDirectory))
        {
            roots.Add(mapPath(context.ProjectDirectory));
        }

        return roots;
    }
}
