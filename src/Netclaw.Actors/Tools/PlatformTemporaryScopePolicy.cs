// -----------------------------------------------------------------------
// <copyright file="PlatformTemporaryScopePolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Configuration;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

internal abstract record ToolAgentCorrection
{
    private ToolAgentCorrection()
    {
    }

    internal sealed record SessionScratchSuggested(
        string SessionDirectory,
        string TemporaryRoot,
        ApprovalShell Shell) : ToolAgentCorrection;
}

internal readonly record struct SessionScratchCallSemantics(
    ApprovalShell Shell,
    string Command,
    bool HasExplicitWorkingDirectory,
    string? ExplicitWorkingDirectory,
    bool Background,
    TimeSpan Timeout);

internal readonly record struct SessionScratchCorrectionKey(
    SessionScratchCallSemantics Call,
    string TemporaryRoot,
    string SessionDirectory);

/// <summary>
/// Identifies advice-only calls that explicitly use the shared platform temp root.
/// This policy grants no authority and does not change the submitted call.
/// </summary>
internal sealed class PlatformTemporaryScopePolicy
{
    private readonly ShellExecutionEnvironment _environment;
    private readonly IPlatformTemporaryPathInspector _pathInspector;
    private readonly IReadOnlyList<PlatformTemporaryRoot> _temporaryRoots;

    internal PlatformTemporaryScopePolicy(
        ShellExecutionEnvironment environment,
        string platformTemporaryRoot,
        IPlatformTemporaryPathInspector pathInspector)
        : this(environment, platformTemporaryRoot, pathInspector, [])
    {
    }

    internal PlatformTemporaryScopePolicy(
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

    internal static PlatformTemporaryScopePolicy Create(ShellExecutionEnvironment environment)
        => new(
            environment,
            Path.GetTempPath(),
            HostPlatformTemporaryPathInspector.Instance,
            environment.PathStyle == ShellPathStyle.Posix ? ["/tmp"] : []);

    internal ToolAgentCorrection.SessionScratchSuggested? Evaluate(
        ShellCommandAnalysis analysis,
        IReadOnlyList<ApprovalCandidate> candidates,
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context)
    {
        if (!analysis.IsResolved
            || analysis.HasDynamicSyntax
            || context.RunScope.InteractiveApproval is not InteractiveApprovalCapability.Available
            || context.Audience != TrustAudience.Personal
            || !TryNormalizeSessionDirectory(context.SessionDirectory, out var sessionDirectory)
            || !TryGetExplicitTemporaryRoot(analysis, arguments, out var temporaryRoot)
            || !AllScopesStayWithinTemporaryRoot(analysis, candidates, temporaryRoot))
        {
            return null;
        }

        var shell = _environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        return new ToolAgentCorrection.SessionScratchSuggested(
            sessionDirectory,
            temporaryRoot.Canonical,
            shell);
    }

    internal bool IsPlatformTemporaryRoot(string? path)
        => TryGetTemporaryRoot(path, out _);

    internal bool IsSafePlatformTemporaryPath(string? path)
    {
        if (!TryNormalizePath(path, out var normalized))
            return false;

        foreach (var root in _temporaryRoots)
        {
            if ((IsWithinRoot(normalized, root.Authored)
                 || IsWithinRoot(normalized, root.Canonical))
                && IsSafeTemporaryPath(normalized, root))
            {
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

            if (!IsSafeTemporaryPath(candidate.Directory, temporaryRoot))
                return false;
        }

        foreach (var command in analysis.Commands)
        {
            if (!HasSafeDirectoryTransitionEffect(command, temporaryRoot)
                && (command.WorkingDirectory is not ShellValueDomain.Exact workingDirectory
                    || !IsSafeTemporaryPath(workingDirectory.Value, temporaryRoot)))
            {
                return false;
            }

            foreach (var argument in command.Clause.Args)
            {
                if (!argument.IsPath && !argument.IsCwdAttribution)
                    continue;

                if (string.IsNullOrWhiteSpace(argument.Resolved)
                    || !IsSafeTemporaryPath(argument.Resolved, temporaryRoot))
                {
                    return false;
                }
            }

            foreach (var redirect in command.Redirects)
            {
                var safe = redirect switch
                {
                    FileRedirectAnalysis file =>
                        HasSafeRedirectTarget(file.Target, temporaryRoot),
                    DescriptorDuplicateRedirectAnalysis => true,
                    DescriptorMoveRedirectAnalysis => true,
                    DescriptorCloseRedirectAnalysis => true,
                    HereDocumentRedirectAnalysis => true,
                    HereStringRedirectAnalysis => true,
                    _ => false
                };
                if (!safe)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool HasSafeDirectoryTransitionEffect(
        CommandOccurrence command,
        PlatformTemporaryRoot temporaryRoot)
        => _environment.Grammar == ShellGrammar.Bash
           && command.WorkingDirectoryEffect is
               ShellWorkingDirectoryEffect.ChangesOnSuccess
           {
               Target: ShellValueDomain.Exact exact
           }
           && IsSafeTemporaryPath(exact.Value, temporaryRoot);

    private bool HasSafeRedirectTarget(
        ShellValueDomain target,
        PlatformTemporaryRoot temporaryRoot)
        => target switch
        {
            ShellValueDomain.Exact exact =>
                IsSafeTemporaryPath(exact.Value, temporaryRoot),
            ShellValueDomain.FiniteSet finite =>
                finite.Values.Count > 0
                && finite.Values.All(path => IsSafeTemporaryPath(path, temporaryRoot)),
            ShellValueDomain.PathPattern pattern =>
                IsSafeTemporaryPath(pattern.CoveringDirectory, temporaryRoot),
            _ => false
        };

    private bool IsSafeTemporaryPath(
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

        return _pathInspector.IsSafeDescendant(
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

    private bool TryNormalizeSessionDirectory(string? path, out string normalized)
    {
        if (!TryNormalizePath(path, out normalized))
            return false;

        return !_pathInspector.ContainsInvalidPathState(normalized, _environment.PathStyle);
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

internal interface IPlatformTemporaryPathInspector
{
    bool TryResolveRoot(string path, ShellPathStyle pathStyle, out string resolvedRoot);

    bool IsSafeDescendant(string root, string path, ShellPathStyle pathStyle);

    bool ContainsInvalidPathState(string path, ShellPathStyle pathStyle);
}

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

    public bool IsSafeDescendant(string root, string path, ShellPathStyle pathStyle)
        => ShellPathRules.UsesHostPathStyle(pathStyle)
           && !PathUtility.ContainsSymlinkSegment(root, path);

    public bool ContainsInvalidPathState(string path, ShellPathStyle pathStyle)
        => !ShellPathRules.UsesHostPathStyle(pathStyle);
}
