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
            if (!IsReviewedDiagnostic(candidate, candidate.SourceOccurrence, prospectiveRoots))
                return false;

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
        string? workingDirectoryOverride = null)
    {
        if (candidate is not
            {
                Shell: { } shell,
                VerbTokens: { }
            }
            || sourceOccurrence is null
            || ShellRedirectPolicyFacts.HasFileWritingRedirect(sourceOccurrence)
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

        return AllPossibleAuthoredPathsStayWithinRoots(
            sourceOccurrence,
            shell,
            safeRoots,
            workingDirectoryOverride);
    }

    internal bool ShortCircuitsCausalIntent(
        ApprovalCandidate candidate,
        CommandOccurrence? sourceOccurrence,
        string intentDirectory,
        ToolInvocationContext context)
    {
        if (context.Audience != TrustAudience.Personal
            || candidate is not
            {
                Shell: ApprovalShell.Bash,
                VerbTokens: { }
            }
            || sourceOccurrence is null
            || string.IsNullOrWhiteSpace(intentDirectory)
            || !IsSafePath(intentDirectory, intentDirectory)
            || !IsReviewedDiagnostic(
                candidate,
                sourceOccurrence,
                [intentDirectory],
                intentDirectory))
        {
            return false;
        }

        return AllEffectivePathsStayWithinIntent(
            sourceOccurrence,
            intentDirectory);
    }

    internal bool ShortCircuits(
        ApprovalCandidate candidate,
        CommandOccurrence? sourceOccurrence,
        string? cwd,
        ToolInvocationContext context)
    {
        var safeRoots = ResolveSafeSpaceRoots(context);
        if (safeRoots.Count == 0
            || !IsReviewedDiagnostic(candidate, sourceOccurrence, safeRoots))
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

    private static bool AllPossibleAuthoredPathsStayWithinRoots(
        CommandOccurrence occurrence,
        ApprovalShell shell,
        IReadOnlyList<string> safeRoots,
        string? workingDirectoryOverride)
    {
        var workingDirectory = workingDirectoryOverride
            ?? (occurrence.WorkingDirectory is ShellValueDomain.Exact exact
                ? exact.Value
                : null);
        var pathStyle = shell == ApprovalShell.Bash
            ? ShellPathStyle.Posix
            : ShellPathStyle.Windows;

        foreach (var argument in occurrence.Arguments)
        {
            if (argument.AuthoredPathShape == ShellPathShape.Unknown)
                continue;
            if (argument.AuthoredPathShape == ShellPathShape.Posix
                    && pathStyle != ShellPathStyle.Posix
                || argument.AuthoredPathShape == ShellPathShape.Windows
                    && pathStyle != ShellPathStyle.Windows)
            {
                return false;
            }

            IReadOnlyList<string> possiblePaths = argument.AuthoredValue switch
            {
                ShellValueDomain.Exact value => [value.Value],
                ShellValueDomain.FiniteSet values => values.Values,
                _ => []
            };
            if (possiblePaths.Count == 0)
                return false;

            foreach (var possiblePath in possiblePaths)
            {
                var resolved = ShellTokenizer.NormalizePathToken(
                    possiblePath,
                    workingDirectory,
                    pathStyle);
                if (string.IsNullOrWhiteSpace(resolved)
                    || !safeRoots.Any(root => IsSafePath(resolved, root)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool AllEffectivePathsStayWithinIntent(
        CommandOccurrence occurrence,
        string intentDirectory)
    {
        foreach (var argument in occurrence.Arguments.Where(static argument =>
                     argument.Argument.IsPath))
        {
            IReadOnlyList<string> values = argument.Value switch
            {
                ShellValueDomain.Exact exact => [exact.Value],
                ShellValueDomain.FiniteSet finite => finite.Values,
                _ => []
            };
            if (values.Count == 0)
                return false;

            foreach (var value in values)
            {
                var resolved = ShellTokenizer.NormalizePathToken(
                    value,
                    intentDirectory,
                    ShellPathStyle.Posix);
                if (string.IsNullOrWhiteSpace(resolved)
                    || !IsSafePath(resolved, intentDirectory))
                {
                    return false;
                }
            }
        }

        foreach (var redirect in occurrence.Redirects.OfType<FileRedirectAnalysis>())
        {
            if (redirect.Mode != FileRedirectMode.Input
                || redirect.Target is not ShellValueDomain.Exact exact)
            {
                return false;
            }

            var resolved = ShellTokenizer.NormalizePathToken(
                exact.Value,
                intentDirectory,
                ShellPathStyle.Posix);
            if (string.IsNullOrWhiteSpace(resolved)
                || !IsSafePath(resolved, intentDirectory))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Resolves the audience-aware safe-space roots for the current
    /// invocation. Personal and Team get <c>session_dir + project_dir</c>;
    /// Public gets <c>session_dir</c> only.
    /// </summary>
    private static IReadOnlyList<string> ResolveSafeSpaceRoots(ToolInvocationContext context)
    {
        var roots = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(PathUtility.Normalize(context.SessionDirectory));

        // Public audience cannot expand its safe space via project_dir —
        // mirrors the file_read read-roots restriction enforced by
        // ScopedFileAccessPolicy. Even a Public session that has somehow
        // populated WorkingContext.ProjectDirectory does not get to use it
        // as a shell safe-space root.
        if (context.Audience != TrustAudience.Public
            && !string.IsNullOrWhiteSpace(context.ProjectDirectory))
        {
            roots.Add(PathUtility.Normalize(context.ProjectDirectory));
        }

        return roots;
    }
}
