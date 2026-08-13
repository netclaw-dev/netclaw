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
    private readonly string? _authoredTemporaryRoot;

    internal PlatformTemporaryScopePolicy(
        ShellExecutionEnvironment environment,
        string platformTemporaryRoot,
        IPlatformTemporaryPathInspector pathInspector)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformTemporaryRoot);
        ArgumentNullException.ThrowIfNull(pathInspector);

        _environment = environment;
        _pathInspector = pathInspector;
        _authoredTemporaryRoot = ShellPathRules.TryNormalize(
            platformTemporaryRoot,
            environment.PathStyle,
            out var authoredRoot)
                ? authoredRoot
                : null;
        TemporaryRoot = _authoredTemporaryRoot is not null
            && pathInspector.TryResolveRoot(
            platformTemporaryRoot,
            environment.PathStyle,
            out var resolvedRoot)
                ? resolvedRoot
                : null;
    }

    internal string? TemporaryRoot { get; }

    internal static PlatformTemporaryScopePolicy Create(ShellExecutionEnvironment environment)
        => new(environment, Path.GetTempPath(), HostPlatformTemporaryPathInspector.Instance);

    internal ToolAgentCorrection.SessionScratchSuggested? Evaluate(
        ShellCommandAnalysis analysis,
        IReadOnlyList<ApprovalCandidate> candidates,
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context)
    {
        if (TemporaryRoot is null
            || !analysis.IsResolved
            || analysis.HasDynamicSyntax
            || context.RunScope.InteractiveApproval is not InteractiveApprovalCapability.Available
            || context.Audience != TrustAudience.Personal
            || !TryNormalizeSessionDirectory(context.SessionDirectory, out var sessionDirectory)
            || !HasExplicitTemporaryIntent(analysis, arguments)
            || !AllScopesStayWithinTemporaryRoot(analysis, candidates))
        {
            return null;
        }

        var shell = _environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        return new ToolAgentCorrection.SessionScratchSuggested(
            sessionDirectory,
            TemporaryRoot,
            shell);
    }

    internal bool IsPlatformTemporaryRoot(string? path)
        => TemporaryRoot is not null
           && TryNormalizePath(path, out var normalized)
           && (PathEquals(normalized, TemporaryRoot)
               || _authoredTemporaryRoot is not null
               && PathEquals(normalized, _authoredTemporaryRoot));

    private bool HasExplicitTemporaryIntent(
        ShellCommandAnalysis analysis,
        IDictionary<string, object?>? arguments)
    {
        var explicitDirectory = ToolArgumentHelper.GetString(arguments, "WorkingDirectory");
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return IsPlatformTemporaryRoot(explicitDirectory);

        if (_environment.Grammar != ShellGrammar.Bash)
            return false;

        return analysis.Commands.Any(command =>
            command.Clause.Args.Any(argument =>
                argument.IsCwdAttribution
                && IsPlatformTemporaryRoot(argument.Resolved))
            && command.WorkingDirectory is ShellValueDomain.Exact exact
            && IsPlatformTemporaryRoot(exact.Value));
    }

    private bool AllScopesStayWithinTemporaryRoot(
        ShellCommandAnalysis analysis,
        IReadOnlyList<ApprovalCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Directory is null)
                continue;

            if (!IsSafeTemporaryPath(candidate.Directory))
                return false;
        }

        foreach (var command in analysis.Commands)
        {
            if (!IsBashDirectoryTransition(command)
                && (command.WorkingDirectory is not ShellValueDomain.Exact workingDirectory
                    || !IsSafeTemporaryPath(workingDirectory.Value)))
            {
                return false;
            }

            foreach (var argument in command.Clause.Args)
            {
                if (!argument.IsPath && !argument.IsCwdAttribution)
                    continue;

                if (string.IsNullOrWhiteSpace(argument.Resolved)
                    || !IsSafeTemporaryPath(argument.Resolved))
                {
                    return false;
                }
            }

            foreach (var redirect in command.Redirects)
            {
                var safe = redirect switch
                {
                    FileRedirectAnalysis file => HasSafeRedirectTarget(file.Target),
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

    private bool IsBashDirectoryTransition(CommandOccurrence command)
        => _environment.Grammar == ShellGrammar.Bash
           && command.Clause.Verb.Tokens.Count > 0
           && command.Clause.Verb.Tokens[0] is "cd" or "chdir";

    private bool HasSafeRedirectTarget(ShellValueDomain target)
        => target switch
        {
            ShellValueDomain.Exact exact => IsSafeTemporaryPath(exact.Value),
            ShellValueDomain.FiniteSet finite =>
                finite.Values.Count > 0 && finite.Values.All(IsSafeTemporaryPath),
            ShellValueDomain.PathPattern pattern =>
                IsSafeTemporaryPath(pattern.CoveringDirectory),
            _ => false
        };

    private bool IsSafeTemporaryPath(string path)
    {
        if (TemporaryRoot is null
            || !TryNormalizePath(path, out var normalized)
            || !TryMapToCanonicalTemporaryPath(normalized, out var canonicalPath))
        {
            return false;
        }

        return _pathInspector.IsSafeDescendant(
            TemporaryRoot,
            canonicalPath,
            _environment.PathStyle);
    }

    private bool TryMapToCanonicalTemporaryPath(string path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (TemporaryRoot is null)
            return false;

        if (IsWithinRoot(path, TemporaryRoot))
        {
            canonicalPath = path;
            return true;
        }

        if (_authoredTemporaryRoot is null
            || !IsWithinRoot(path, _authoredTemporaryRoot))
        {
            return false;
        }

        var relative = path[_authoredTemporaryRoot.Length..]
            .TrimStart('/', '\\');
        canonicalPath = relative.Length == 0
            ? TemporaryRoot
            : TemporaryRoot.TrimEnd('/', '\\') +
              (_environment.PathStyle == ShellPathStyle.Windows ? '\\' : '/') +
              relative;
        return true;
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
        if (!UsesHostPathStyle(pathStyle)
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
        => UsesHostPathStyle(pathStyle)
           && !PathUtility.ContainsSymlinkSegment(root, path);

    public bool ContainsInvalidPathState(string path, ShellPathStyle pathStyle)
        => !UsesHostPathStyle(pathStyle);

    private static bool UsesHostPathStyle(ShellPathStyle pathStyle)
        => pathStyle == ShellPathStyle.Windows
            ? OperatingSystem.IsWindows()
            : !OperatingSystem.IsWindows();
}

internal static class ShellPathRules
{
    internal static bool TryNormalize(
        string? path,
        ShellPathStyle pathStyle,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (pathStyle == ShellPathStyle.Posix)
        {
            if (path[0] != '/')
                return false;

            normalized = NormalizeSegments(path, '/', "/");
            return normalized.Length > 0;
        }

        var windowsPath = path.Replace('/', '\\');
        var rootLength = GetWindowsRootLength(windowsPath);
        if (rootLength == 0)
            return false;

        var root = windowsPath[..rootLength];
        normalized = NormalizeSegments(windowsPath[rootLength..], '\\', root);
        return normalized.Length > 0;
    }

    internal static bool Equals(string left, string right, ShellPathStyle pathStyle)
        => string.Equals(
            left,
            right,
            pathStyle == ShellPathStyle.Windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    internal static bool IsWithinRoot(
        string candidate,
        string root,
        ShellPathStyle pathStyle)
    {
        if (Equals(candidate, root, pathStyle))
            return true;

        var separator = pathStyle == ShellPathStyle.Windows ? '\\' : '/';
        var prefix = root.EndsWith(separator) ? root : root + separator;
        return candidate.StartsWith(
            prefix,
            pathStyle == ShellPathStyle.Windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string NormalizeSegments(string path, char separator, string root)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count == 0)
                    return string.Empty;

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
            return root;

        return root.EndsWith(separator)
            ? root + string.Join(separator, segments)
            : root + separator + string.Join(separator, segments);
    }

    private static int GetWindowsRootLength(string path)
    {
        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] == '\\')
        {
            return 3;
        }

        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
            return 0;

        var serverEnd = path.IndexOf('\\', 2);
        if (serverEnd <= 2)
            return 0;

        var shareEnd = path.IndexOf('\\', serverEnd + 1);
        return shareEnd < 0 ? path.Length : shareEnd + 1;
    }
}
