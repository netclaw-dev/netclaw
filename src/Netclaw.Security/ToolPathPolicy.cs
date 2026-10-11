// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Configuration;
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
    // denied. A hint denies a path token alone, and any token with a high-risk
    // verb. The config directory is not a hint: the agent may read its config
    // (decision D6), and the write list still denies a shell write to it.
    private static readonly string[] DefaultLayoutHints =
    [
        ".netclaw/keys",
        "secrets.json",
    ];

    private readonly ShellCommandAnalyzer _analyzer;
    private readonly HashSet<string> _commandIndicators;
    private readonly IReadOnlyList<Regex> _anchoredIndicators;
    private readonly HashSet<string> _guardedDirectoryMarkers;
    private readonly IReadOnlyList<string> _guardedDirectories;

    // The shell set plus each guarded directory. It applies when the parser
    // cannot prove the whole source, so the trusted-root check may not see a path.
    private readonly FileSystemAuthority _unprovedShell;

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
        _anchoredIndicators = [];
        _guardedDirectories = GuardedDirectories(materialized, materialized);
        _guardedDirectoryMarkers = BuildGuardedDirectoryMarkers(_guardedDirectories);
        _unprovedShell = FileSystem;
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

    /// <param name="environment">The host shell.</param>
    /// <param name="writeDeniedPaths">Paths that a write meets.</param>
    /// <param name="readDeniedPaths">Paths that a read meets.</param>
    /// <param name="shellIndicatorPaths">Paths that shell text meets.</param>
    /// <param name="homeAnchoredPaths">
    /// Entries of the shell list whose command text indicators are only their
    /// home-anchored spellings (<c>~/.ssh</c>, <c>$HOME/.ssh</c>,
    /// <c>$env:USERPROFILE\.ssh</c>) and their absolute path, each ending at a
    /// name boundary. A bare file name would also deny a workspace path or a
    /// pattern. The resolved-path checks cover every other spelling.
    /// </param>
    public ToolPathPolicy(
        ShellExecutionEnvironment environment,
        IEnumerable<string> writeDeniedPaths,
        IEnumerable<string> readDeniedPaths,
        IEnumerable<string> shellIndicatorPaths,
        IEnumerable<string>? homeAnchoredPaths = null)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _analyzer = new ShellCommandAnalyzer(environment);
        var writeList = writeDeniedPaths.ToList();
        var readList = readDeniedPaths.ToList();
        var shellList = shellIndicatorPaths.ToList();
        FileSystem = new FileSystemAuthority(writeList, readList, shellList);
        var anchoredList = (homeAnchoredPaths ?? []).ToList();
        var anchoredKeys = anchoredList.Select(PathUtility.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _commandIndicators = BuildCommandIndicators(shellList
            .Where(path => !anchoredKeys.Contains(PathUtility.Normalize(path))));
        _anchoredIndicators = BuildAnchoredIndicators(anchoredList, DefaultAnchors());
        _guardedDirectories = GuardedDirectories(writeList, readList);
        _guardedDirectoryMarkers = BuildGuardedDirectoryMarkers(_guardedDirectories);
        _unprovedShell = _guardedDirectories.Count == 0
            ? FileSystem
            : new FileSystemAuthority([], [], [.. shellList, .. _guardedDirectories]);
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

    // The variables that name a base directory in shell text, for both Bash and
    // PowerShell. Text is compared with its backslashes turned into slashes and
    // without case, so "$env:USERPROFILE\\.ssh" and "~/.SSH" are the same spelling.
    internal readonly record struct HomeAnchor(string BasePath, string[] Spellings);

    internal static readonly string[] HomeSpellings = ["~", "$HOME", "${HOME}", "$env:USERPROFILE", "${env:USERPROFILE}", "%USERPROFILE%"];
    internal static readonly string[] AppDataSpellings = ["$env:APPDATA", "${env:APPDATA}", "%APPDATA%"];
    internal static readonly string[] ProgramDataSpellings = ["$env:PROGRAMDATA", "${env:PROGRAMDATA}", "%PROGRAMDATA%"];

    private static List<HomeAnchor> DefaultAnchors()
    {
        var anchors = new List<HomeAnchor>
        {
            new(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), HomeSpellings)
        };
        if (OperatingSystem.IsWindows())
        {
            anchors.Add(new(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), AppDataSpellings));
            anchors.Add(new(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), ProgramDataSpellings));
        }

        return anchors;
    }

    /// <summary>
    /// Builds the text indicators of home-anchored entries: each anchor spelling
    /// followed by the entry below its base, and the absolute path. A match ends
    /// at a name boundary, so "~/.sshrc" is not "~/.ssh" while "~/.ssh:/x" is.
    /// </summary>
    internal static List<Regex> BuildAnchoredIndicators(IEnumerable<string> paths, IReadOnlyList<HomeAnchor> anchors)
    {
        var texts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            foreach (var form in new[] { path, PathUtility.Normalize(path) })
            {
                var slashPath = form.Replace('\\', '/').TrimEnd('/');
                texts.Add(slashPath);
                foreach (var anchor in anchors)
                {
                    var anchorBase = anchor.BasePath.Replace('\\', '/').TrimEnd('/');
                    if (anchorBase.Length == 0
                        || !slashPath.StartsWith(anchorBase + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relative = slashPath[(anchorBase.Length + 1)..];
                    foreach (var spelling in anchor.Spellings)
                        texts.Add(spelling + "/" + relative);
                }
            }
        }

        return texts
            .Select(text => new Regex(
                Regex.Escape(text) + "(?![A-Za-z0-9_.\\-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }

    /// <summary>
    /// Returns the text markers of each write-protected directory that holds a
    /// read-protected path, for example the config directory that holds
    /// <c>secrets.json</c>.
    /// </summary>
    /// <remarks>
    /// Shell text that names such a directory stays denied, as before decision
    /// D6. Decision D6 exempts only an exact path argument of a read-only
    /// program that names one file below the directory
    /// (<see cref="ScreenText"/>). Program text that names the directory
    /// (<c>jq 'import "secrets" {search: "dir"}'</c>, <c>python3 -c</c>) stays
    /// denied.
    /// </remarks>
    private static HashSet<string> BuildGuardedDirectoryMarkers(IReadOnlyList<string> guardedDirectories)
        => BuildCommandIndicators(guardedDirectories)
            .Where(marker => marker.Contains('/', StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<string> GuardedDirectories(
        IReadOnlyList<string> writeDenied,
        IReadOnlyList<string> readDenied)
    {
        var readPaths = readDenied.Select(PathUtility.Normalize).ToList();
        return writeDenied
            .Select(PathUtility.Normalize)
            .Where(directory => readPaths.Any(path =>
                !string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)
                && CanonicalPath.IsWithin(path, directory, CanonicalPath.HostStyle, ignoreCase: true)))
            .ToList();
    }

    /// <summary>
    /// Returns the text that the guarded-directory screen reads: the command text
    /// without each exact path argument of a read-only program that names one
    /// file below a guarded directory, plus the unquoted value of each other
    /// argument.
    /// </summary>
    /// <remarks>
    /// SECURITY: only such an argument leaves the text screen. A glob, a word
    /// that <see cref="HasUnmodeledExpansion"/> names, a <c>..</c> segment, the
    /// directory itself, an option value, a redirect, and program text keep the
    /// denial. The unquoted values
    /// show a directory that quotes split in the text (<c>"dir/con'fig'"</c>).
    /// The read-only programs are policy data
    /// (<see cref="ShellVerbPolicyData.ReadOnlyOperandVerbs"/>). Without a parser
    /// proof, the structured check still denies the removed operand.
    /// </remarks>
    private string ScreenText(ShellCommandAnalysis analysis, string slashCommand)
    {
        var text = slashCommand;
        var values = new List<string>();
        foreach (var occurrence in analysis.Commands)
        {
            var readOnly = occurrence is { IsComplete: true, Assignments.Count: 0, Clause.Verb: { IsDynamic: false, Tokens: [var program, ..] } }
                && ShellVerbPolicyData.ReadOnlyOperandVerbs.Contains(program);
            foreach (var argument in occurrence.Arguments)
            {
                var raw = argument.Element.Raw.Replace('\\', '/');
                if (readOnly
                    && argument is { Argument.IsPath: true, Argument.Kind: not ArgKind.Glob, Value: ShellValueDomain.Exact }
                    && !raw.Split('/').Contains("..")
                    && !HasUnmodeledExpansion(raw)
                    && NamesOneFileBelowGuardedDirectory(argument.Argument.Resolved)
                    && text.IndexOf(raw, StringComparison.Ordinal) is var at and >= 0)
                {
                    text = text.Remove(at, raw.Length);
                    continue;
                }

                values.AddRange(argument.Value switch
                {
                    ShellValueDomain.Exact exact => [exact.Value],
                    ShellValueDomain.FiniteSet finite => finite.Values,
                    _ => []
                });
            }
        }

        return string.Join('\n', [text, .. values.Select(value => value.Replace('\\', '/'))]);
    }

    /// <summary>
    /// Returns true when a raw shell word holds a form whose exact value the
    /// parser does not model: a brace (<c>{a,b}</c>), ANSI-C quotes
    /// (<c>$'\x73'</c>), or locale quotes (<c>$"x"</c>).
    /// </summary>
    /// <remarks>
    /// SECURITY: the parser reports such a word as one exact value, but Bash
    /// expands a brace to more words and decodes the quotes. For example, the
    /// parser reports <c>$'webhooks'</c> as <c>$webhooks</c>. A decision D6
    /// exemption must not trust that value.
    /// </remarks>
    internal static bool HasUnmodeledExpansion(string raw)
        => raw.Contains('{', StringComparison.Ordinal)
           || raw.Contains("$'", StringComparison.Ordinal)
           || raw.Contains("$\"", StringComparison.Ordinal);

    // SECURITY: shell text can spell a guarded directory with "//", "/./", or
    // "name/../". The marker check also reads the text with these forms
    // collapsed, so each spelling stays denied. It also reads the original text,
    // because a collapsed "dir/../x" no longer names the directory. A trailing
    // "/." needs no rule: the directory name comes before it, so a marker matches.
    private bool MentionsGuardedDirectory(string text)
    {
        var collapsed = CollapseDotSegments(text);
        return _guardedDirectoryMarkers.Any(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || collapsed.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex ParentSegment = new("/[^/]+/\\.\\./", RegexOptions.CultureInvariant);

    private static string CollapseDotSegments(string text)
    {
        string previous;
        do
        {
            previous = text;
            text = ParentSegment.Replace(
                text.Replace("//", "/", StringComparison.Ordinal).Replace("/./", "/", StringComparison.Ordinal),
                "/");
        }
        while (!string.Equals(text, previous, StringComparison.Ordinal));

        return text;
    }

    // An exemption is an allow check, so it compares with ordinal case except on Windows.
    private static readonly bool AllowIgnoresCase = OperatingSystem.IsWindows();

    private bool NamesOneFileBelowGuardedDirectory(string? resolved)
        => CanonicalPath.TryCreateHost(resolved, relativeBase: null, out var path)
           && _guardedDirectories.Any(directory =>
               !CanonicalPath.IsWithin(directory, path.Value, CanonicalPath.HostStyle, AllowIgnoresCase)
               && CanonicalPath.IsWithin(path.Value, directory, CanonicalPath.HostStyle, AllowIgnoresCase));

    /// <summary>
    /// Returns true when a parser-canonical shell path is protected. A path in
    /// another style than this shell cannot be checked, so it counts as protected (R5).
    /// </summary>
    internal bool IsShellDeniedProjectedPath(CanonicalPath path)
        => path.Style != Environment.PathStyle
           || FileSystem.IsProtected(path.Value, PathOperation.Shell);

    private static bool IsShellDenied(FileSystemAuthority shell, string path)
        => shell.IsProtected(path, PathOperation.Shell);

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
        // SECURITY: the trusted-root check sees each path only when the parser
        // proves the whole source. Without that proof, a guarded directory stays
        // shell-denied as a whole, as before decision D6.
        var proved = analysis.IsResolved && !analysis.HasDynamicSyntax;
        var shell = proved ? FileSystem : _unprovedShell;

        if (!string.IsNullOrWhiteSpace(workingDirectory) && IsShellDenied(shell, workingDirectory))
            return true;

        var tokens = LegacyShellTextScan.Tokenize(command).ToList();
        var slashCommand = command.Replace('\\', '/');
        foreach (var indicator in _commandIndicators)
        {
            if (slashCommand.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (_anchoredIndicators.Any(indicator => indicator.IsMatch(slashCommand)))
            return true;

        // Each mention of a guarded directory stays denied, as when the whole
        // config directory was a shell indicator. Only an exact read operand leaves
        // the text (decision D6). Without a parser proof, the structured check of
        // the operand uses the shell set plus each guarded directory.
        var screened = ScreenText(analysis, slashCommand);
        if (MentionsGuardedDirectory(screened))
            return true;

        if (StructuredAnalysisReferencesDeniedPath(analysis, shell)
            || PlainWordLinkReachesDeniedPath(analysis, shell))
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
            if (normalized is not null && IsShellDenied(shell, normalized))
                return true;

            var expanded = PathUtility.ExpandHome(token).Replace('\\', '/');
            if (DefaultLayoutHints.Any(hint => expanded.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // The default config directory keeps its hint with a high-risk verb, also
        // when an operator moves the Netclaw root. A read operand leaves it only
        // when the default directory is the live one.
        var hasHighRiskVerb = tokens.Any(token =>
            ShellVerbPolicyData.HighRiskVerbs.Contains(LegacyShellTextScan.TrimShellPunctuation(token)));
        if (hasHighRiskVerb
            && (DefaultLayoutHints.Any(hint => slashCommand.Contains(hint, StringComparison.OrdinalIgnoreCase))
                || screened.Contains(".netclaw/config", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private bool StructuredAnalysisReferencesDeniedPath(
        ShellCommandAnalysis analysis,
        FileSystemAuthority shell)
    {
        if (analysis.Failure != ShellAnalysisFailure.None)
            return false;

        foreach (var occurrence in analysis.Commands)
        {
            foreach (var argument in occurrence.Clause.Args)
            {
                if (argument.IsPath
                    && !string.IsNullOrWhiteSpace(argument.Resolved)
                    && IsProvedValueDenied(shell, argument.Resolved))
                {
                    return true;
                }
            }

            foreach (var effective in occurrence.Arguments)
            {
                if (effective.Element.IsPath
                    && DomainReferencesDeniedPath(effective.Value, shell))
                {
                    return true;
                }

                if (DomainReferencesDeniedPath(effective.AuthoredFileSystemValue, shell))
                {
                    return true;
                }
            }

            foreach (var redirect in occurrence.Redirects)
            {
                if (redirect is FileRedirectAnalysis file
                    && DomainReferencesDeniedPath(file.Target, shell))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when a plain word after the program word names a link in the
    /// directory of its command, and the link target is protected.
    /// </summary>
    /// <remarks>
    /// SECURITY: the parser is lexical, so <c>keylink</c> is a plain word, not a
    /// path, also when it names a link to a protected directory. A verb grant
    /// covers later words without a name for each one, so this screen checks the
    /// link target of each such word, command word or argument (<c>keys2</c>).
    /// It only denies a protected target. Grant coverage of the link target is
    /// a scope of the approval candidate (<see cref="ShellApprovalMatcher"/>, #2375).
    /// </remarks>
    private static bool PlainWordLinkReachesDeniedPath(
        ShellCommandAnalysis analysis,
        FileSystemAuthority shell)
    {
        if (analysis.Failure != ShellAnalysisFailure.None)
            return false;

        foreach (var occurrence in analysis.Commands)
        {
            // An unproved directory names no entry. Such a command is exact
            // consent only, so no grant covers it.
            var directory = (occurrence.WorkingDirectory as ShellValueDomain.Exact)?.Value;
            if (FindLinkWords(occurrence, directory).Any(link => IsShellDenied(shell, link)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the path of each word after the program word that names a link
    /// directly in <paramref name="directory"/>. The protected-path screen and
    /// the approval scopes (<see cref="ShellApprovalMatcher"/>, #2375) read the
    /// same words through this one loop.
    /// </summary>
    internal static IEnumerable<string> FindLinkWords(CommandOccurrence occurrence, string? directory)
    {
        var programWord = true;
        foreach (var element in occurrence.Clause.Elements)
        {
            // The program word is a PATH lookup, not a file of the directory.
            if (programWord && element.Role == ClauseElementRole.Verb)
            {
                programWord = false;
                continue;
            }

            if (ShellGrantFileWords.NamesLink(element.Value, directory, out var link))
                yield return link;
        }
    }

    private bool DomainReferencesDeniedPath(ShellValueDomain domain, FileSystemAuthority shell)
        => domain switch
        {
            ShellValueDomain.Exact exact =>
                !string.IsNullOrWhiteSpace(exact.Value)
                && IsProvedValueDenied(shell, exact.Value),
            ShellValueDomain.FiniteSet finite => finite.Values.Any(value =>
                !string.IsNullOrWhiteSpace(value)
                && IsProvedValueDenied(shell, value)),
            ShellValueDomain.PathPattern pattern =>
                !string.IsNullOrWhiteSpace(pattern.CoveringDirectory)
                && (IsShellDenied(shell, pattern.CoveringDirectory) || GlobMayReachDeniedPath(pattern, shell)),
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
    private bool GlobMayReachDeniedPath(ShellValueDomain.PathPattern pattern, FileSystemAuthority shell)
    {
        var glob = ShellGlobScope.AsGlobPattern(pattern);
        return glob is not null
               && shell.GetProtectedPaths(PathOperation.Shell)
                   .Concat(DefaultCredentialStorePaths())
                   .Any(target => MatchIsDenied(glob, target, shell));
    }

    // The match is the target itself, a directory that contains it (the glob
    // reads below it), or the ancestor of the target at the glob depth. The
    // ancestor gets the decision of that literal directory.
    private static bool MatchIsDenied(ShellValueDomain.PathPattern glob, string target, FileSystemAuthority shell)
    {
        var match = ShellGlobScope.MatchPathToward(glob, target);
        return match is not null
               && (string.Equals(match, target, StringComparison.OrdinalIgnoreCase) || IsShellDenied(shell, match));
    }

    /// <summary>
    /// Returns true when a proved path value is protected, or when it names the
    /// default credential store as the text hints do.
    /// </summary>
    /// <remarks>
    /// SECURITY: the text hints read the authored text. A decoded value, such as
    /// <c>~/.netclaw/$'\x6beys'/key-1.xml</c> (ShellSyntaxTree 0.4.0-beta.19),
    /// can name the store with no hint in the text. The proved value gets the
    /// same hints, also when an operator moves the Netclaw root.
    /// </remarks>
    private static bool IsProvedValueDenied(FileSystemAuthority shell, string value)
        => IsShellDenied(shell, value)
           || DefaultLayoutHints.Any(hint =>
               value.Replace('\\', '/').Contains(hint, StringComparison.OrdinalIgnoreCase));

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
