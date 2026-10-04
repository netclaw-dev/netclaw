// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security.Authorization.Filesystem;
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// Builds the process filesystem authority from the protected-path lists, and
/// checks shell command text for references to protected paths.
/// </summary>
/// <remarks>
/// The protected lists are policy data. <see cref="FileSystem"/> owns the three
/// protected sets (write, read, shell) and every path comparison. This class
/// keeps only the shell text heuristics. They scan raw substrings of the command
/// text, so directory-scoped entries (e.g. the config dir) over-block commands
/// whose text merely mentions them. That is the accepted trade-off for keeping
/// the control plane unreachable.
/// </remarks>
public sealed class ToolPathPolicy
{
    // The default-layout text hints. They apply even when an operator moves the
    // Netclaw root, so a command that names the default credential store stays
    // denied. A credential hint denies a path token alone; every hint denies with
    // a high-risk verb.
    private static readonly (string Fragment, bool IsCredentialStore)[] DefaultLayoutHints =
    [
        (".netclaw/config", false),
        (".netclaw/keys", true),
        ("secrets.json", true),
    ];

    private readonly ShellCommandAnalyzer _analyzer;
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
        FileSystem = new FileSystemAuthority(materialized, materialized, materialized);
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
        var shellList = shellIndicatorPaths.ToList();
        FileSystem = new FileSystemAuthority(writeDeniedPaths, readDeniedPaths, shellList);
        _commandIndicators = BuildCommandIndicators(shellList);
    }

    public ShellExecutionEnvironment Environment { get; }

    /// <summary>The filesystem authority that owns the protected sets for this process.</summary>
    internal FileSystemAuthority FileSystem { get; }

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
    /// Returns true when a parser-canonical shell path is protected. A path in
    /// another style than this shell cannot be checked, so it counts as protected (R5).
    /// </summary>
    internal bool IsShellDeniedProjectedPath(CanonicalPath path)
        => path.Style != Environment.PathStyle
           || FileSystem.IsProtected(path.Value, PathOperation.Shell);

    private bool IsShellDenied(string path)
        => FileSystem.IsProtected(path, PathOperation.Shell);

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

        if (!string.IsNullOrWhiteSpace(workingDirectory) && IsShellDenied(workingDirectory))
            return true;

        var tokens = LegacyShellTextScan.Tokenize(command).ToList();
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

            // The authority resolves every link segment, so a planted link under
            // an approved directory cannot hide a protected target. A resolution
            // failure counts as protected.
            var normalized = PathUtility.NormalizeShellPath(
                token,
                workingDirectory,
                Environment.PathStyle);
            if (normalized is not null && IsShellDenied(normalized))
                return true;

            var expanded = PathUtility.ExpandHome(token).Replace('\\', '/');
            if (DefaultLayoutHints.Any(hint =>
                    hint.IsCredentialStore
                    && expanded.Contains(hint.Fragment, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (DefaultLayoutHints.Any(hint => slashCommand.Contains(hint.Fragment, StringComparison.OrdinalIgnoreCase))
            && tokens.Any(token => ShellVerbPolicyData.HighRiskVerbs.Contains(LegacyShellTextScan.TrimShellPunctuation(token))))
        {
            return true;
        }

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
                    && IsShellDenied(argument.Resolved))
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
                && IsShellDenied(exact.Value),
            ShellValueDomain.FiniteSet finite => finite.Values.Any(value =>
                !string.IsNullOrWhiteSpace(value)
                && IsShellDenied(value)),
            ShellValueDomain.PathPattern pattern =>
                !string.IsNullOrWhiteSpace(pattern.CoveringDirectory)
                && (IsShellDenied(pattern.CoveringDirectory) || GlobMayReachDeniedPath(pattern)),
            _ => false
        };

    /// <summary>
    /// Returns true when a glob word can reach a protected path or the default
    /// credential store.
    /// </summary>
    /// <remarks>
    /// SECURITY (decision D5, option A): a glob word reaches each path below its
    /// covering directory that its segments can match. When the segments can match
    /// a protected path, or a directory that contains one, the word gets the
    /// decision of that literal path. The match is lexical. Netclaw does not list
    /// the directory or follow links here, so a link below the covering directory
    /// that leads to a protected path is an accepted gap.
    /// </remarks>
    private bool GlobMayReachDeniedPath(ShellValueDomain.PathPattern pattern)
    {
        var glob = ShellGlobScope.AsGlobPattern(pattern);
        return glob is not null
               && FileSystem.GetProtectedPaths(PathOperation.Shell)
                   .Concat(DefaultCredentialStorePaths())
                   .Any(target => MatchIsDenied(glob, target));
    }

    // The match is the target itself, a directory that contains it (the glob
    // reads below it), or the ancestor of the target at the glob depth. The
    // ancestor gets the decision of that literal directory.
    private bool MatchIsDenied(ShellValueDomain.PathPattern glob, string target)
    {
        var match = ShellGlobScope.MatchPathToward(glob, target);
        return match is not null
               && (string.Equals(match, target, StringComparison.OrdinalIgnoreCase) || IsShellDenied(match));
    }

    // The default credential store of the home directory. The text hints deny these
    // paths even when an operator moves the Netclaw root, so a glob gets the same rule.
    private IEnumerable<string> DefaultCredentialStorePaths()
    {
        var home = Environment.HomeDirectory;
        if (string.IsNullOrEmpty(home))
            return [];

        return
        [
            PathUtility.Normalize(Path.Combine(home, ".netclaw", "keys")),
            PathUtility.Normalize(Path.Combine(home, ".netclaw", "config", "secrets.json"))
        ];
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
