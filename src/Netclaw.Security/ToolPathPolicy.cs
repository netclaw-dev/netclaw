// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using ShellSyntaxTree;

namespace Netclaw.Security;

internal readonly record struct CanonicalShellPath
{
    private CanonicalShellPath(string value, ShellPathStyle pathStyle)
    {
        Value = value;
        PathStyle = pathStyle;
    }

    internal string Value { get; }

    internal ShellPathStyle PathStyle { get; }

    internal static bool TryCreate(
        string? value,
        ShellPathStyle pathStyle,
        out CanonicalShellPath path)
    {
        path = default;
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
        {
            return false;
        }

        if (!ShellPathRules.TryNormalize(value, pathStyle, out var normalized))
            return false;

        path = new CanonicalShellPath(normalized, pathStyle);
        return true;
    }
}

/// <summary>
/// Evaluates whether a file path is denied for agent tool access.
/// </summary>
/// <remarks>
/// Three independent deny surfaces: write (<see cref="IsDenied"/>), read
/// (<see cref="IsReadDenied"/>), and shell indicators
/// (<see cref="CommandReferencesDeniedPath"/>). The shell indicator list is
/// scanned as raw substrings of the command text, so directory-scoped entries
/// (e.g. the config dir) over-block commands whose text merely mentions them —
/// that is the accepted trade-off for keeping the control plane unreachable.
/// </remarks>
public sealed class ToolPathPolicy
{
    private readonly ShellCommandAnalyzer _analyzer;
    private readonly HashSet<string> _writeDeniedPaths;
    private readonly HashSet<string> _readDeniedPaths;
    private readonly HashSet<string> _shellDeniedPaths;
    private readonly HashSet<string> _commandIndicators;

    public ToolPathPolicy(IEnumerable<string> deniedPaths)
        : this(ShellExecutionEnvironmentDefaults.Bash, deniedPaths)
    {
    }

    public ToolPathPolicy(
        ShellExecutionEnvironment environment,
        IEnumerable<string> deniedPaths)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _analyzer = new ShellCommandAnalyzer(environment);
        var materialized = deniedPaths.ToList();
        _writeDeniedPaths = BuildNormalizedSet(materialized);
        _readDeniedPaths = _writeDeniedPaths;
        _shellDeniedPaths = _writeDeniedPaths;
        _commandIndicators = BuildCommandIndicators(materialized);
    }

    public ToolPathPolicy(
        IEnumerable<string> writeDeniedPaths,
        IEnumerable<string> readDeniedPaths,
        IEnumerable<string> shellIndicatorPaths)
        : this(
            ShellExecutionEnvironmentDefaults.Bash,
            writeDeniedPaths,
            readDeniedPaths,
            shellIndicatorPaths)
    {
    }

    public ToolPathPolicy(
        ShellExecutionEnvironment environment,
        IEnumerable<string> writeDeniedPaths,
        IEnumerable<string> readDeniedPaths,
        IEnumerable<string> shellIndicatorPaths)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _analyzer = new ShellCommandAnalyzer(environment);
        _writeDeniedPaths = BuildNormalizedSet(writeDeniedPaths);
        _readDeniedPaths = BuildNormalizedSet(readDeniedPaths);
        var shellList = shellIndicatorPaths.ToList();
        _shellDeniedPaths = BuildNormalizedSet(shellList);
        _commandIndicators = BuildCommandIndicators(shellList);
    }

    public ShellExecutionEnvironment Environment { get; }

    private static HashSet<string> BuildNormalizedSet(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalized = PathUtility.Normalize(path);
            set.Add(normalized);

            // macOS keeps /etc, /var, /tmp as symlinks into /private, so a denied
            // path that traverses one resolves to a different real path. The deny
            // checks canonicalize the *candidate* path (TryResolveSymlinksInPath /
            // TryResolveSymlinkTarget); without the resolved denied form here, a
            // candidate resolving to /private/etc/... would slip past a /etc deny.
            //
            // Construction skips a resolution failure (the lexical form above is
            // still added); the deny CHECKS fail closed on the same failure. A
            // startup-time resolution throw must not crash the process, and the
            // lexical entry alone still denies exact and lexical-child matches.
            if (TryResolveCanonicalForDenySet(normalized, out var canonical))
                set.Add(canonical);
        }

        return set;
    }

    // Construction-only: resolve a denied path's canonical form, but never crash the
    // policy build if resolution throws. The lexical form is already in the set, and
    // the runtime deny checks (IsDeniedAgainst / CommandReferencesDeniedPath) fail
    // CLOSED on a resolution exception — so swallowing here is safe for construction.
    private static bool TryResolveCanonicalForDenySet(string path, out string canonical)
    {
        try
        {
            return TryResolveSymlinksInPath(path, out canonical);
        }
        catch
        {
            canonical = string.Empty;
            return false;
        }
    }

    private static HashSet<string> BuildCommandIndicators(IEnumerable<string> paths)
    {
        var materialized = paths.ToList();
        var normalizedPaths = materialized.Select(PathUtility.Normalize).ToList();
        var indicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in materialized.Concat(normalizedPaths))
        {
            var slashPath = path.Replace('\\', '/');
            indicators.Add(slashPath);

            var netclawSegmentIdx = slashPath.IndexOf("/.netclaw/", StringComparison.OrdinalIgnoreCase);
            if (netclawSegmentIdx >= 0)
                indicators.Add(slashPath[(netclawSegmentIdx + 1)..]);

            var fileName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains('.', StringComparison.Ordinal))
                indicators.Add(fileName);
        }

        return indicators;
    }

    /// <summary>
    /// Returns true if the given path is denied for write by policy.
    /// Normalizes the path (resolves "..", removes trailing separators) before checking.
    /// </summary>
    public bool IsDenied(string path)
        => IsDeniedAgainst(path, _writeDeniedPaths);

    /// <summary>
    /// Returns true if the given path is denied for structured file reads.
    /// Shell indicators remain independent because a structured read names one
    /// exact operation and path.
    /// </summary>
    public bool IsReadDenied(string path)
        => IsDeniedAgainst(path, _readDeniedPaths);

    internal bool IsShellDeniedProjectedPath(
        CanonicalShellPath path)
        => path.PathStyle != Environment.PathStyle
           || IsShellDenied(path.Value);

    private bool IsShellDenied(string path)
        => IsDeniedAgainst(path, _shellDeniedPaths);

    private static bool IsDeniedAgainst(string path, HashSet<string> deniedSet)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (PathUtility.TryNormalize(path, null, out var normalized) && IsDeniedNormalized(normalized, deniedSet))
            return true;

        try
        {
            // TryResolveSymlinkTarget only resolves the final path element. A path
            // whose INTERMEDIATE directory is a symlink into a denied location
            // (e.g. /tmp/x -> ~/.netclaw/config, then /tmp/x/netclaw.json) would
            // slip past that check. Mirror the shell side (CommandReferencesDeniedPath)
            // by also walking the path segment by segment — same infrastructure.
            if (TryResolveSymlinkTarget(path, out var resolvedTarget)
                && IsDeniedNormalized(resolvedTarget, deniedSet))
            {
                return true;
            }

            return TryResolveSymlinksInPath(path, out var canonical)
                && IsDeniedNormalized(canonical, deniedSet);
        }
        catch
        {
            // This method is the SOLE backstop for interactive Personal reads
            // (IsReadDenied has no other gate above it). An undetermined
            // resolution must deny, not silently allow — the same defect class
            // as the double-drive fail-open fixed for #1724. Fail closed.
            return true;
        }
    }

    /// <summary>
    /// Returns true if the given shell command string contains a reference to any denied path.
    /// Checks both the original path strings and their normalized forms.
    /// This is a defense-in-depth heuristic — not bulletproof against obfuscation.
    /// </summary>
    public bool CommandReferencesDeniedPath(string command, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        return CommandReferencesDeniedPath(
            _analyzer.Analyze(command, workingDirectory));
    }

    public bool CommandReferencesDeniedPath(ShellCommandAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!ReferenceEquals(analysis.Environment, Environment))
            throw new ArgumentException(
                "The command analysis belongs to another shell environment.",
                nameof(analysis));

        var command = analysis.Source;
        var workingDirectory = analysis.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(workingDirectory)
            && IsDeniedAgainst(workingDirectory, _shellDeniedPaths))
        {
            return true;
        }

        var tokens = ShellTokenizer.Tokenize(command).ToList();
        var slashCommand = command.Replace('\\', '/');
        foreach (var indicator in _commandIndicators)
        {
            if (slashCommand.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (StructuredAnalysisReferencesDeniedPath(analysis))
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (!LooksLikePath(token))
                continue;

            var normalized = ShellTokenizer.NormalizePathToken(
                token,
                workingDirectory,
                Environment.PathStyle);
            if (normalized is not null && IsDeniedNormalized(normalized, _shellDeniedPaths))
            {
                return true;
            }

            // Defense-in-depth against symlink escalation under a directory-
            // scoped approval. Path.GetFullPath (used by NormalizePathToken)
            // collapses `.`/`..` but does NOT resolve symlinks in any path
            // component. Without this pass, a user who approves /home/safe/
            // can be tricked by a planted /home/safe/leak -> /etc symlink:
            // the approval gate sees /home/safe/leak/passwd as "within" the
            // approved root and waves it through, and the static path check
            // here would never see /etc unless we resolve link targets along
            // every component of the path.
            if (normalized is not null)
            {
                try
                {
                    if (TryResolveSymlinksInPath(normalized, out var canonical)
                        && IsDeniedNormalized(canonical, _shellDeniedPaths))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Fail closed: an undetermined resolution means we cannot
                    // rule out this token reaching a denied path via symlink,
                    // so treat the command as referencing one.
                    return true;
                }
            }

            var expanded = PathUtility.ExpandHome(token);
            if (expanded.Contains("secrets.json", StringComparison.OrdinalIgnoreCase)
                || expanded.Contains(".netclaw/keys", StringComparison.OrdinalIgnoreCase)
                || expanded.Contains(".netclaw\\keys", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ContainsProtectedPathHint(slashCommand) && ContainsHighRiskVerb(tokens))
            return true;

        return false;
    }

    private bool StructuredAnalysisReferencesDeniedPath(
        ShellCommandAnalysis analysis)
    {
        if (analysis.Failure != ShellAnalysisFailure.None)
            return false;

        foreach (var occurrence in analysis.Commands)
        {
            foreach (var argument in occurrence.Clause.Args)
            {
                if (argument.IsPath
                    && !string.IsNullOrWhiteSpace(argument.Resolved)
                    && IsDeniedAgainst(argument.Resolved, _shellDeniedPaths))
                {
                    return true;
                }
            }

            foreach (var effective in occurrence.Arguments)
            {
                if (effective.Element.IsPath
                    && DomainReferencesDeniedPath(effective.Value))
                {
                    return true;
                }

                if (DomainReferencesDeniedPath(effective.AuthoredFileSystemValue))
                {
                    return true;
                }
            }

            foreach (var redirect in occurrence.Redirects)
            {
                if (redirect is FileRedirectAnalysis file
                    && DomainReferencesDeniedPath(file.Target))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool DomainReferencesDeniedPath(ShellValueDomain domain)
        => domain switch
        {
            ShellValueDomain.Exact exact =>
                !string.IsNullOrWhiteSpace(exact.Value)
                && IsDeniedAgainst(exact.Value, _shellDeniedPaths),
            ShellValueDomain.FiniteSet finite => finite.Values.Any(value =>
                !string.IsNullOrWhiteSpace(value)
                && IsDeniedAgainst(value, _shellDeniedPaths)),
            ShellValueDomain.PathPattern pattern =>
                !string.IsNullOrWhiteSpace(pattern.CoveringDirectory)
                && IsDeniedAgainst(pattern.CoveringDirectory, _shellDeniedPaths),
            _ => false
        };

    private static bool ContainsProtectedPathHint(string slashCommand)
    {
        return slashCommand.Contains(".netclaw/config", StringComparison.OrdinalIgnoreCase)
            || slashCommand.Contains(".netclaw/keys", StringComparison.OrdinalIgnoreCase)
            || slashCommand.Contains("secrets.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsHighRiskVerb(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            var verb = ShellTokenizer.TrimShellPunctuation(token);
            if (ShellTokenizer.HighRiskVerbs.Contains(verb))
                return true;
        }

        return false;
    }

    private static bool IsDeniedNormalized(string candidate, HashSet<string> deniedSet)
    {
        foreach (var denied in deniedSet)
        {
            if (IsSamePathOrChild(candidate, denied))
                return true;
        }

        return false;
    }

    private static bool IsSamePathOrChild(string candidate, string denied)
    {
        if (!candidate.StartsWith(denied, StringComparison.OrdinalIgnoreCase))
            return false;

        if (candidate.Length == denied.Length)
            return true;

        var boundary = candidate[denied.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    // Callers own exception policy here on purpose: BuildNormalizedSet (startup)
    // skips a failed resolution, while the deny-check call sites (IsDeniedAgainst,
    // CommandReferencesDeniedPath) fail closed. A blanket catch here would hide
    // that distinction and force every caller back to the same (wrong) answer.
    private static bool TryResolveSymlinksInPath(string path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(path))
            return false;

        // Walk the path component by component, resolving any directory
        // or file symlinks encountered. ResolveLinkTarget(returnFinalTarget:
        // true) follows the chain to a non-link, but only operates on the
        // entity it's invoked against — it does not see symlinks earlier
        // in the path. Hence the explicit segment walk.
        var fullPath = Path.GetFullPath(path);
        // Seed the builder with the full root (drive + separator on Windows,
        // "/" on Unix) and split only the REMAINDER after the root. Splitting
        // the whole path re-emits the drive segment ("C:"), which the root
        // already provides — appending it again yields "C:\C:\Users\..." so
        // every Directory.Exists/File.Exists probe below misses and symlink
        // resolution silently no-ops, failing the deny open. See #1724.
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        sb.Append(root);

        foreach (var segment in segments)
        {
            if (sb.Length > 0 && sb[^1] != Path.DirectorySeparatorChar)
                sb.Append(Path.DirectorySeparatorChar);
            sb.Append(segment);

            var partial = sb.ToString();
            if (Directory.Exists(partial))
            {
                var target = new DirectoryInfo(partial).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    sb.Clear();
                    sb.Append(target.FullName);
                }
            }
            else if (File.Exists(partial))
            {
                var target = new FileInfo(partial).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    sb.Clear();
                    sb.Append(target.FullName);
                }

                break;
            }
        }

        canonical = PathUtility.Normalize(sb.ToString());
        return !string.IsNullOrEmpty(canonical) && !string.Equals(canonical, PathUtility.Normalize(fullPath), StringComparison.Ordinal);
    }

    // Only IsDeniedAgainst calls this; it owns exception policy (fails closed).
    // See the comment on TryResolveSymlinksInPath for why this does not catch.
    private static bool TryResolveSymlinkTarget(string path, out string normalizedTarget)
    {
        normalizedTarget = string.Empty;

        if (File.Exists(path))
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                return false;

            normalizedTarget = PathUtility.Normalize(target.FullName);
            return true;
        }

        if (Directory.Exists(path))
        {
            var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                return false;

            normalizedTarget = PathUtility.Normalize(target.FullName);
            return true;
        }

        return false;
    }

    private static bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token.StartsWith("-", StringComparison.Ordinal))
            return false;

        return token.Contains('/', StringComparison.Ordinal)
            || token.Contains('\\', StringComparison.Ordinal)
            || token.StartsWith(".", StringComparison.Ordinal)
            || token.StartsWith("~", StringComparison.Ordinal)
            || token.Contains(':', StringComparison.Ordinal)
            || token.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

}
