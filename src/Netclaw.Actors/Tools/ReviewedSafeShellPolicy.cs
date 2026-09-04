// -----------------------------------------------------------------------
// <copyright file="ReviewedSafeShellPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Applies reviewed-safe shell coverage after shell command policy and file protection pass.
/// The policy covers a reviewed diagnostic only when all parser-owned source and scope guards pass.
///
/// Delegates every filesystem decision to <see cref="PathAccessPolicy"/>.
/// ShellSyntaxTree supplies syntax and path facts; this type only decides
/// whether a phrase qualifies for reviewed-diagnostic handling.
///
/// The policy never relaxes tool capability, shell command policy, or file protection.
/// It only supplies approval coverage for phrases that the bundled catalog reviews.
/// </summary>
internal sealed class ReviewedSafeShellPolicy
{
    private readonly SafeVerbList _safeVerbs;
    private readonly PathAccessPolicy _pathAccessPolicy;

    public ReviewedSafeShellPolicy(
        SafeVerbList safeVerbs,
        PathAccessPolicy pathAccessPolicy)
    {
        _safeVerbs = safeVerbs;
        _pathAccessPolicy = pathAccessPolicy;
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

        var pathStyle = candidates[0].Shell == ApprovalShell.PowerShell
            ? ShellPathStyle.Windows
            : ShellPathStyle.Posix;
        if (_pathAccessPolicy.EvaluateReviewedShellPath(fullCwd, context, pathStyle).Allowed)
        {
            return false;
        }

        var declaration = _pathAccessPolicy.Evaluate(
            fullCwd,
            context,
            PathAccessPolicy.FileOperation.DeclareProjectScope);
        if (!declaration.Allowed)
            return false;

        foreach (var candidate in candidates)
        {
            var resolvedPaths = ResolveCompatibilityPaths(
                candidate,
                candidate.SourceOccurrence,
                fullCwd);
            if (!IsReviewedDiagnostic(
                    candidate,
                    candidate.SourceOccurrence,
                    context,
                    resolvedPaths,
                    declaration.CanonicalPath))
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

            if (!_pathAccessPolicy.EvaluateReviewedShellPath(
                    fullDirectory,
                    context,
                    pathStyle,
                    declaration.CanonicalPath).Allowed)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsReviewedDiagnostic(
        ApprovalCandidate candidate,
        CommandOccurrence? sourceOccurrence,
        ToolInvocationContext context,
        ShellPolicyResolvedPathView? resolvedPaths,
        string? proposedProjectRoot = null,
        bool includeTrustedRootInLinkCheck = true)
    {
        if (!IsReviewedDiagnosticSyntax(candidate, sourceOccurrence, resolvedPaths, out var shell))
            return false;

        return AllAuthoredPathsStayWithinRoots(
            resolvedPaths,
            shell,
            context,
            proposedProjectRoot,
            includeTrustedRootInLinkCheck);
    }

    internal bool ShortCircuitsCausalIntent(
        ShellPolicyCandidate projected,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
    {
        var candidate = projected.Candidate;
        if (context.Audience != TrustAudience.Personal
            || candidate is not
            {
                Shell: ApprovalShell.Bash,
                VerbTokens: { }
            }
            || pathFacts.Intent?.ResolutionBase is not
            {
                State: ShellPolicyPathResolutionState.Known,
                Path: { } intentPath
            }
            || !IsWithinCausalIntent(intentPath.Value, intentPath.Value)
            || !IsReviewedDiagnosticSyntax(
                candidate,
                projected.SourceOccurrence,
                pathFacts.Intent,
                out _))
        {
            return false;
        }

        return AllPathsStayWithinIntent(
            pathFacts.Intent,
            intentPath.Value);
    }

    internal bool ShortCircuits(
        ApprovalCandidate candidate,
        string? cwd,
        ToolInvocationContext context)
    {
        var projected = new ShellPolicyCandidate(
            new ShellPolicyCandidateId(0),
            candidate.Directory is null ? candidate with { Directory = cwd } : candidate,
            candidate.SourceOccurrence);
        var pathStyle = OperatingSystem.IsWindows()
            ? ShellPathStyle.Windows
            : ShellPathStyle.Posix;
        var facts = ShellPolicyPathFacts.Create([projected], pathStyle);
        return ShortCircuits(projected, facts[projected.Id.Value], context);
    }

    internal bool ShortCircuits(
        ShellPolicyCandidate projected,
        ShellPolicyCandidatePathFacts pathFacts,
        ToolInvocationContext context)
    {
        var candidate = projected.Candidate;
        if (!IsReviewedDiagnostic(
                candidate,
                projected.SourceOccurrence,
                context,
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
        return _pathAccessPolicy.EvaluateReviewedShellPath(
            realPath.Value,
            context,
            pathStyle).Allowed;
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

    private bool IsReviewedDiagnosticSyntax(
        ApprovalCandidate candidate,
        CommandOccurrence? sourceOccurrence,
        ShellPolicyResolvedPathView? resolvedPaths,
        out ApprovalShell shell)
    {
        shell = default;
        if (candidate is not
            {
                Shell: { } candidateShell,
                VerbTokens: { }
            }
            || sourceOccurrence is null
            || HasFileWritingRedirect(resolvedPaths)
            || HasUnprovedNonFileSystemSemantics(resolvedPaths)
            || !_safeVerbs.TryMatchReviewedDiagnostic(
                candidateShell,
                candidate.VerbTokens,
                out var matchedTokenCount)
            || sourceOccurrence.Arguments.Any(argument =>
                argument.Element.PrecedingVerbElementCount < matchedTokenCount))
        {
            return false;
        }

        shell = candidateShell;
        return true;
    }

    private bool AllAuthoredPathsStayWithinRoots(
        ShellPolicyResolvedPathView? resolvedPaths,
        ApprovalShell shell,
        ToolInvocationContext context,
        string? proposedProjectRoot,
        bool includeTrustedRootInLinkCheck)
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
                || fact.Source.Domain is not
                    (ShellValueDomain.Exact or ShellValueDomain.FiniteSet)
                || fact.State != ShellPolicyPathResolutionState.Known
                || fact.Paths.Count == 0
                || fact.Paths.Any(path =>
                    !_pathAccessPolicy.EvaluateReviewedShellPath(
                        path.Value,
                        context,
                        path.PathStyle,
                        proposedProjectRoot,
                        includeTrustedRootInLinkCheck).Allowed))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllPathsStayWithinIntent(
        ShellPolicyResolvedPathView? resolvedPaths,
        string intentDirectory)
    {
        if (resolvedPaths is null)
            return false;

        foreach (var fact in resolvedPaths.Facts.Where(static fact =>
                     fact.Source.Origin is ShellPolicyPathOrigin.AuthoredArgument
                         or ShellPolicyPathOrigin.EffectiveArgument))
        {
            if (fact.Source.Domain is not
                    (ShellValueDomain.Exact or ShellValueDomain.FiniteSet)
                || fact.State != ShellPolicyPathResolutionState.Known
                || fact.Paths.Count == 0
                || fact.Paths.Any(path => !IsWithinCausalIntent(path.Value, intentDirectory)))
            {
                return false;
            }
        }

        foreach (var fact in resolvedPaths.Facts.Where(static fact =>
                     fact.Source.Origin == ShellPolicyPathOrigin.Redirect))
        {
            if (fact.Source.RedirectMode != FileRedirectMode.Input
                || fact.Source.Domain is not ShellValueDomain.Exact
                || fact.State != ShellPolicyPathResolutionState.Known
                || fact.Paths.Count != 1
                || !IsWithinCausalIntent(fact.Paths[0].Value, intentDirectory))
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

    // A parser-derived intent is approval scope, not a trusted root. Permit a
    // root alias such as macOS /tmp,
    // while still rejecting linked descendants below it.
    private static bool IsWithinCausalIntent(string path, string intentRoot)
        => IsWithinShellRoot(path, intentRoot, ShellPathStyle.Posix, includeRoot: false);

    private static bool IsWithinShellRoot(
        string path,
        string root,
        ShellPathStyle pathStyle,
        bool includeRoot = true)
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
                           normalizedPath,
                           includeRoot));
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

}
