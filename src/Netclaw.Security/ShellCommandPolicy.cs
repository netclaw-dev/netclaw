// -----------------------------------------------------------------------
// <copyright file="ShellCommandPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Security;

/// <summary>
/// Result of evaluating a shell command against the hard deny list.
/// </summary>
public sealed record ShellCommandDecision(bool Allowed, string? DenyReason = null, DenyCategory? DenyCategory = null)
{
    public static ShellCommandDecision Allow() => new(true);

    public static ShellCommandDecision Deny(string reason, DenyCategory category) => new(false, reason, category);
}

/// <summary>
/// Evaluates shell commands against a configurable hard deny list.
/// Denied commands are categorically blocked and cannot be approved.
/// Uses structural matching (tokenized verb + subcommand + flags),
/// not substring matching on the raw command string.
/// </summary>
public sealed class ShellCommandPolicy
{
    private readonly ShellCommandAnalyzer _analyzer;
    private readonly IReadOnlyList<DenyPattern> _denyPatterns;
    private readonly IReadOnlyList<RawStringPattern> _rawStringPatterns;

    public ShellCommandPolicy(IEnumerable<string>? additionalDenyPatterns = null)
        : this(
            ShellExecutionEnvironmentDefaults.Bash,
            additionalDenyPatterns,
            overrideRules: null)
    {
    }

    public ShellCommandPolicy(
        ShellExecutionEnvironment environment,
        IEnumerable<string>? additionalDenyPatterns = null)
        : this(environment, additionalDenyPatterns, overrideRules: null)
    {
    }

    /// <summary>
    /// Constructs the policy with both legacy string-based deny patterns
    /// (kept for back-compat with <c>toolConfig.HardDenyPatterns</c>) and
    /// structured operator override rules from <see cref="HardDenyRule"/>.
    /// Both inputs are additive — shipped defaults
    /// (<see cref="DefaultDenyPatterns"/>, <see cref="DefaultRawStringPatterns"/>)
    /// are always present and cannot be removed via either input.
    /// </summary>
    public ShellCommandPolicy(
        IEnumerable<string>? additionalDenyPatterns,
        IEnumerable<HardDenyRule>? overrideRules)
        : this(
            ShellExecutionEnvironmentDefaults.Bash,
            additionalDenyPatterns,
            overrideRules)
    {
    }

    public ShellCommandPolicy(
        ShellExecutionEnvironment environment,
        IEnumerable<string>? additionalDenyPatterns,
        IEnumerable<HardDenyRule>? overrideRules)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _analyzer = new ShellCommandAnalyzer(environment);
        var structured = new List<DenyPattern>(DefaultDenyPatterns);
        var raw = new List<RawStringPattern>(DefaultRawStringPatterns);

        if (additionalDenyPatterns is not null)
        {
            foreach (var pattern in additionalDenyPatterns)
            {
                var parsed = ParseDenyPattern(pattern);
                if (parsed is not null)
                    structured.Add(parsed);
            }
        }

        if (overrideRules is not null)
        {
            foreach (var rule in overrideRules)
            {
                rule.Validate();
                TranslateRule(rule, structured, raw);
            }
        }

        _denyPatterns = structured;
        _rawStringPatterns = raw;
    }

    public ShellExecutionEnvironment Environment { get; }

    public ShellCommandAnalysis Analyze(
        string command,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _analyzer.Analyze(command, workingDirectory);
    }

    /// <summary>
    /// Analyzes a command with the launch facts of one call, including the managed
    /// temporary variables that the launcher sets for <paramref name="temporary"/>.
    /// </summary>
    public ShellCommandAnalysis Analyze(
        string command,
        string? workingDirectory,
        ManagedTemporaryLocation? temporary)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _analyzer.Analyze(command, workingDirectory, temporary);
    }

    private static void TranslateRule(
        HardDenyRule rule,
        List<DenyPattern> structured,
        List<RawStringPattern> raw)
    {
        if (!string.IsNullOrWhiteSpace(rule.RawText))
        {
            raw.Add(new RawStringPattern(rule.RawText, rule.Reason, rule.Category));
            return;
        }

        if (!string.IsNullOrWhiteSpace(rule.VerbPrefix))
        {
            structured.Add(new VerbPrefixDenyPattern(rule.VerbPrefix, rule.Reason, rule.Category));
            return;
        }

        if (rule.Verb is { Count: > 0 })
        {
            // Refined verb-chain match: verb-chain prefix + optional argFlags +
            // optional firstPath. Falls back to plain VerbChainDenyPattern when
            // no refinements are present.
            if (rule.ArgFlags is not { Count: > 0 } && rule.FirstPath is null)
            {
                structured.Add(new VerbChainDenyPattern(rule.Verb, rule.Reason, rule.Category));
                return;
            }

            structured.Add(new RefinedVerbChainDenyPattern(
                rule.Verb,
                rule.ArgFlags,
                rule.FirstPath,
                rule.Reason,
                rule.Category));
        }
    }

    /// <summary>
    /// Evaluates a shell command (possibly compound) against the deny list.
    /// If any segment of a compound command matches, the entire command is denied.
    /// Recursively scans bash -c / sh -c inner commands.
    /// </summary>
    public ShellCommandDecision Evaluate(string command)
        => Evaluate(command, workingDirectory: null);

    public ShellCommandDecision Evaluate(
        string command,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ShellCommandDecision.Allow();

        return Evaluate(Analyze(command, workingDirectory));
    }

    public ShellCommandDecision Evaluate(ShellCommandAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!ReferenceEquals(analysis.Environment, Environment))
            throw new ArgumentException(
                "The command analysis belongs to another shell environment.",
                nameof(analysis));

        // Check raw-string patterns first (fork bombs, etc.) before splitting,
        // since compound-command splitting destroys the original formatting.
        var rawDecision = EvaluateRawString(analysis.Source);
        if (!rawDecision.Allowed)
            return rawDecision;

        return EvaluateStructuralAnalysis(analysis);
    }

    /// <summary>
    /// Evaluates a Bash command with the same structural path that production
    /// uses on Linux and macOS.
    /// </summary>
    internal ShellCommandDecision EvaluateBash(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ShellCommandDecision.Allow();

        return Evaluate(command);
    }

    private ShellCommandDecision EvaluateStructuralAnalysis(
        ShellCommandAnalysis analysis)
    {
        var denyOnlyDecision = EvaluateDenyOnlyClauses(analysis.DenyOnlyClauses);
        if (!denyOnlyDecision.Allowed)
            return denyOnlyDecision;

        if (analysis.Failure == ShellAnalysisFailure.Unresolved || analysis.Commands.Count == 0)
        {
            // The raw-text scan decides first, so every denial of the legacy
            // scan keeps its reason. The parser screen then checks each Bash
            // list element, which the scan does not split.
            var legacyDecision = EvaluateLegacySegments(analysis.Source);
            if (!legacyDecision.Allowed)
                return legacyDecision;

            return EvaluateClauses(analysis.ScreenClauses);
        }

        foreach (var occurrence in analysis.Commands)
        {
            var decision = EvaluateClause(occurrence.Clause);
            if (!decision.Allowed)
                return decision;
        }

        return ShellCommandDecision.Allow();
    }

    internal ShellCommandDecision EvaluateDenyOnlyClauses(
        IReadOnlyList<ShellSyntaxTree.Clause> clauses)
    {
        foreach (var clause in clauses)
        {
            var decision = EvaluateDenyOnlyClause(clause);
            if (!decision.Allowed)
                return decision;
        }

        return ShellCommandDecision.Allow();
    }

    private ShellCommandDecision EvaluateClauses(IReadOnlyList<ShellSyntaxTree.Clause> clauses)
    {
        foreach (var clause in clauses)
        {
            var decision = EvaluateClause(clause);
            if (!decision.Allowed)
                return decision;
        }

        return ShellCommandDecision.Allow();
    }

    private ShellCommandDecision EvaluateLegacySegments(string command)
    {
        // The approval matcher does not persist unresolved syntax. Keep the
        // legacy scan here so known deny forms still fail at this boundary.
        foreach (var segment in LegacyShellTextScan.GetAllCommandSegments(command))
        {
            var tokens = LegacyShellTextScan.Tokenize(segment)
                .Select(static token => DenyToken.Known(token))
                .ToList();
            foreach (var pattern in _denyPatterns)
            {
                if (pattern.Matches(tokens))
                    return ShellCommandDecision.Deny(pattern.Reason, pattern.Category);
            }
        }

        return ShellCommandDecision.Allow();
    }

    private ShellCommandDecision EvaluateRawString(string command)
    {
        foreach (var pattern in _rawStringPatterns)
        {
            if (command.Contains(pattern.Pattern, StringComparison.Ordinal))
                return ShellCommandDecision.Deny(pattern.Reason, pattern.Category);
        }

        return ShellCommandDecision.Allow();
    }

    private ShellCommandDecision EvaluateClause(ShellSyntaxTree.Clause clause)
    {
        var tokens = new List<DenyToken>(
            clause.Verb.Tokens.Count + clause.Args.Count + clause.Redirects.Count);
        if (clause.Verb.CanonicalVerb is { Length: > 0 } canonicalVerb)
        {
            tokens.Add(DenyToken.Known(canonicalVerb));
            tokens.AddRange(clause.Verb.Tokens.Skip(1)
                .Select(static token => DenyToken.Known(token)));
        }
        else
        {
            tokens.AddRange(clause.Verb.Tokens
                .Select(static token => DenyToken.Known(token)));
        }
        if (Environment.Grammar == ShellGrammar.PowerShell
            && clause.Elements.Count > 0)
        {
            // PowerShell can bind a parameter and its value from one source
            // element, such as -Recurse:$false. The projected Args split that
            // element and lose the binding. Hard-deny rules need the authored
            // element so they do not reinterpret an explicit false switch.
            tokens.AddRange(clause.Elements
                .Where(static element => element.Role == ShellSyntaxTree.ClauseElementRole.Argument)
                .Select(static element => DenyToken.FromPowerShellElement(element)));
        }
        else
        {
            tokens.AddRange(clause.Args
                .Where(static arg => !arg.IsCwdAttribution)
                .Select(static arg => DenyToken.Known(arg.Raw)));
        }
        tokens.AddRange(clause.Redirects
            .Where(static redirect => !string.IsNullOrEmpty(redirect.Target))
            .Select(static redirect => DenyToken.Known(redirect.Target)));

        if (tokens.Count == 0)
            return ShellCommandDecision.Allow();

        foreach (var pattern in _denyPatterns)
        {
            if (pattern.Matches(tokens))
                return ShellCommandDecision.Deny(pattern.Reason, pattern.Category);
        }

        return ShellCommandDecision.Allow();
    }

    private ShellCommandDecision EvaluateDenyOnlyClause(ShellSyntaxTree.Clause clause)
    {
        var tokens = new List<DenyToken>(
            clause.Verb.Tokens.Count + clause.Elements.Count);
        if (clause.Verb.CanonicalVerb is { Length: > 0 } canonicalVerb)
        {
            tokens.Add(DenyToken.Known(canonicalVerb));
            tokens.AddRange(clause.Verb.Tokens.Skip(1)
                .Select(static token => DenyToken.Known(token)));
        }
        else
        {
            tokens.AddRange(clause.Verb.Tokens
                .Select(static token => DenyToken.Known(token)));
        }
        tokens.AddRange(clause.Elements
            .Where(static element =>
                element.Role == ShellSyntaxTree.ClauseElementRole.Argument)
            .Select(static element => DenyToken.FromPowerShellElement(element)));

        foreach (var pattern in _denyPatterns)
        {
            if (pattern.Matches(tokens))
                return ShellCommandDecision.Deny(pattern.Reason, pattern.Category);
        }

        return ShellCommandDecision.Allow();
    }

    private static DenyPattern? ParseDenyPattern(string raw)
    {
        var tokens = LegacyShellTextScan.Tokenize(raw).ToList();
        if (tokens.Count == 0)
            return null;

        return new LegacyVerbChainDenyPattern(tokens, raw, DenyCategory.CustomDeny);
    }

    // ── Default deny patterns ──

    private static readonly IReadOnlyList<DenyPattern> DefaultDenyPatterns =
    [
        // Self-destruction: killing the netclaw daemon
        new VerbChainDenyPattern(["netclaw", "daemon", "stop"], "Cannot stop the daemon from within a session", DenyCategory.SelfDestructive),
        new VerbChainDenyPattern(["netclaw", "daemon", "kill"], "Cannot kill the daemon from within a session", DenyCategory.SelfDestructive),
        new VerbChainDenyPattern(["systemctl", "stop", "netclaw"], "Cannot stop the netclaw service", DenyCategory.SelfDestructive),
        new VerbChainDenyPattern(["systemctl", "kill", "netclaw"], "Cannot kill the netclaw service", DenyCategory.SelfDestructive),

        // Owner decision D2: a process kill that names the Netclaw daemon stays
        // denied. Any other kill is an ordinary command that a grant can cover.
        new DaemonProcessKillDenyPattern("Cannot kill the Netclaw daemon from within a session", DenyCategory.SelfDestructive),

        // Privilege escalation: the agent must never elevate privileges.
        // If it needs elevated access, the daemon should run as a user with those permissions.
        new PrivilegeEscalationDenyPattern("Cannot escalate privileges from within a session", DenyCategory.PrivilegeEscalation),

        // System-destructive: rm -rf on root or home
        new RmRfRootDenyPattern("Cannot remove root or home directories", DenyCategory.SystemDestructive),

        // Filesystem destruction (mkfs, mkfs.ext4, mkfs.xfs, etc.)
        new VerbPrefixDenyPattern("mkfs", "Cannot create filesystems", DenyCategory.SystemDestructive),
    ];

    /// <summary>
    /// Patterns matched against the raw command string before tokenization.
    /// Used for patterns (like fork bombs) that don't tokenize cleanly.
    /// </summary>
    private static readonly IReadOnlyList<RawStringPattern> DefaultRawStringPatterns =
    [
        new(":(){ :|:& };:", "Fork bomb detected", DenyCategory.SystemDestructive),
        new(":(){:|:&};:", "Fork bomb detected", DenyCategory.SystemDestructive),
    ];

    internal sealed record RawStringPattern(string Pattern, string Reason, DenyCategory Category);

    // ── Pattern types ──

    internal readonly record struct DenyToken(
        string Value,
        string AuthoredValue,
        bool IsKnown,
        string? ParameterName = null)
    {
        public static DenyToken Known(string value, string? authoredValue = null)
        {
            var authored = authoredValue ?? value;
            var parameterName = TryReadParameter(
                authored,
                out var authoredParameterName,
                out _)
                && IsStaticParameterName(authoredParameterName)
                    ? authoredParameterName
                    : null;
            return new(value, authored, IsKnown: true, parameterName);
        }

        public static DenyToken FromPowerShellElement(ShellSyntaxTree.ClauseElement element)
        {
            var isKnown = element.Kind is ShellSyntaxTree.ArgKind.Literal
                or ShellSyntaxTree.ArgKind.Glob
                or ShellSyntaxTree.ArgKind.Tilde;
            // PowerShell reports its two Boolean false spellings as EnvVar.
            // Treat only the exact source-authenticated Boolean as known.
            // Other environment and dynamic values keep their unknown status.
            isKnown |= ShellCommandPolicy.IsExactlyAuthoredFalse(element.Value, element.Raw);
            var parameterName = TryReadParameter(
                element.Raw,
                out var authoredParameterName,
                out _)
                && IsStaticParameterName(authoredParameterName)
                    ? authoredParameterName
                    : null;

            return new DenyToken(
                element.Value,
                element.Raw,
                isKnown,
                parameterName);
        }

        private static bool IsStaticParameterName(string parameterName)
        {
            if (parameterName.Length == 0)
                return false;

            foreach (var character in parameterName)
            {
                if (!char.IsLetterOrDigit(character) && character != '-')
                    return false;
            }

            return true;
        }
    }

    internal abstract record DenyPattern(string Reason, DenyCategory Category)
    {
        public abstract bool Matches(IReadOnlyList<DenyToken> tokens);
    }

    /// <summary>
    /// Matches when the command starts with the specified verb chain (case-insensitive).
    /// </summary>
    internal sealed record VerbChainDenyPattern(
        IReadOnlyList<string> VerbChain,
        string Reason,
        DenyCategory Category) : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<DenyToken> tokens)
        {
            if (tokens.Count < VerbChain.Count)
                return false;

            for (var i = 0; i < VerbChain.Count; i++)
            {
                if (!tokens[i].IsKnown)
                    return false;

                var tokenVerb = LegacyShellTextScan.TrimShellPunctuation(tokens[i].Value);
                if (!string.Equals(tokenVerb, VerbChain[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Matches a legacy configured string pattern against decoded values or
    /// the exact authored spelling of an unresolved PowerShell value.
    /// </summary>
    internal sealed record LegacyVerbChainDenyPattern(
        IReadOnlyList<string> VerbChain,
        string Reason,
        DenyCategory Category) : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<DenyToken> tokens)
        {
            if (tokens.Count < VerbChain.Count)
                return false;

            for (var i = 0; i < VerbChain.Count; i++)
            {
                var token = tokens[i];
                var value = token.IsKnown ? token.Value : token.AuthoredValue;
                var normalized = LegacyShellTextScan.TrimShellPunctuation(value);
                if (!string.Equals(
                        normalized,
                        VerbChain[i],
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Matches when the first token starts with the given prefix (case-insensitive).
    /// Used for commands like mkfs.ext4, mkfs.xfs, etc.
    /// </summary>
    internal sealed record VerbPrefixDenyPattern(
        string Prefix,
        string Reason,
        DenyCategory Category) : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<DenyToken> tokens)
        {
            if (tokens.Count == 0 || !tokens[0].IsKnown)
                return false;

            var verb = LegacyShellTextScan.TrimShellPunctuation(tokens[0].Value);
            return verb.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Matches a process kill (kill, killall, pkill, Stop-Process) whose operand
    /// text names the Netclaw daemon, for example <c>pkill netclawd</c>,
    /// <c>Stop-Process -Name netclaw</c>, or <c>kill $(cat ~/.netclaw/daemon.pid)</c>.
    /// </summary>
    /// <remarks>
    /// Owner decision D2: the blanket kill denial blocked test servers that the
    /// agent started. Any other kill is an ordinary command that a grant can
    /// cover, and the prompt shows its process ID or name. The verbs and the
    /// daemon name are policy data; the rule reads no option grammar. An
    /// operand with an unknown value (<c>kill "$pid"</c>) is not denied here.
    /// </remarks>
    internal sealed record DaemonProcessKillDenyPattern(string Reason, DenyCategory Category)
        : DenyPattern(Reason, Category)
    {
        private const string DaemonName = "netclaw";

        private static readonly HashSet<string> KillVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "kill", "killall", "pkill", "Stop-Process"
        };

        public override bool Matches(IReadOnlyList<DenyToken> tokens)
        {
            if (tokens.Count == 0 || !tokens[0].IsKnown)
                return false;

            var verb = LegacyShellTextScan.TrimShellPunctuation(tokens[0].Value);
            return KillVerbs.Contains(verb)
                   && tokens.Skip(1).Any(static token =>
                       token.AuthoredValue.Contains(DaemonName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Matches privilege escalation commands (sudo, su, doas).
    /// These are categorically denied because the agent should never
    /// need to elevate privileges beyond the daemon user.
    /// </summary>
    internal sealed record PrivilegeEscalationDenyPattern(string Reason, DenyCategory Category)
        : DenyPattern(Reason, Category)
    {
        private static readonly HashSet<string> EscalationVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "sudo", "su", "doas"
        };

        public override bool Matches(IReadOnlyList<DenyToken> tokens)
        {
            if (tokens.Count == 0 || !tokens[0].IsKnown)
                return false;

            var verb = LegacyShellTextScan.TrimShellPunctuation(tokens[0].Value);
            if (EscalationVerbs.Contains(verb))
                return true;

            if (!string.Equals(verb, "Start-Process", StringComparison.OrdinalIgnoreCase))
                return false;

            for (var i = 1; i < tokens.Count; i++)
            {
                if (!tokens[i].IsKnown)
                    continue;

                var token = LegacyShellTextScan.TrimShellPunctuation(tokens[i].Value);
                if (!TryReadParameter(token, out var parameterName, out var inlineValue)
                    || !IsParameterAbbreviation(parameterName, "Verb"))
                {
                    continue;
                }

                if (inlineValue is not null && IsRunAsValue(inlineValue))
                    return true;

                if (inlineValue is null
                    && i + 1 < tokens.Count
                    && tokens[i + 1].IsKnown
                    && IsRunAsValue(tokens[i + 1].Value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRunAsValue(string token)
            => string.Equals(
                TrimStaticQuotes(LegacyShellTextScan.TrimShellPunctuation(token)),
                "RunAs",
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches rm -rf targeting root (/) or home (~/ or $HOME).
    /// </summary>
    internal sealed record RmRfRootDenyPattern(string Reason, DenyCategory Category)
        : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<DenyToken> tokens)
        {
            if (tokens.Count < 2 || !tokens[0].IsKnown)
                return false;

            var verb = LegacyShellTextScan.TrimShellPunctuation(tokens[0].Value);
            var isBashRemove = string.Equals(verb, "rm", StringComparison.OrdinalIgnoreCase);
            var isPowerShellRemove = string.Equals(
                verb,
                "Remove-Item",
                StringComparison.OrdinalIgnoreCase);
            if (!isBashRemove && !isPowerShellRemove)
                return false;

            var hasRecursive = false;
            var hasForce = false;
            var hasDangerousTarget = false;

            for (var i = 1; i < tokens.Count; i++)
            {
                var fact = tokens[i];
                var token = fact.Value;

                if (isPowerShellRemove && fact.ParameterName is { } parameterName)
                {
                    if (IsParameterAbbreviation(parameterName, "Recurse")
                        && !IsExplicitFalse(fact))
                    {
                        hasRecursive = true;
                    }

                    if (IsParameterAbbreviation(parameterName, "Force")
                        && !IsExplicitFalse(fact))
                    {
                        hasForce = true;
                    }
                }
                else if (fact.IsKnown
                         && token.StartsWith('-')
                         && !token.StartsWith("--", StringComparison.Ordinal))
                {
                    if (token.Contains('r', StringComparison.Ordinal)
                        || token.Contains('R', StringComparison.Ordinal))
                    {
                        hasRecursive = true;
                    }

                    if (token.Contains('f', StringComparison.Ordinal))
                        hasForce = true;
                }
                else if (fact.IsKnown && token.Equals("--recursive", StringComparison.Ordinal))
                {
                    hasRecursive = true;
                }
                else if (fact.IsKnown && token.Equals("--force", StringComparison.Ordinal))
                {
                    hasForce = true;
                }

                // Check for dangerous targets
                if ((fact.IsKnown && IsDangerousRemoveTarget(token))
                    || (!fact.IsKnown
                        && IsAuthoredHomeVariable(fact.AuthoredValue)
                        && IsDangerousRemoveTarget(fact.AuthoredValue)))
                    hasDangerousTarget = true;
            }

            return hasRecursive
                   && hasDangerousTarget
                   && (isPowerShellRemove || hasForce);
        }

        private static bool IsDangerousRemoveTarget(string token)
        {
            token = TrimStaticQuotes(LegacyShellTextScan.TrimShellPunctuation(token));
            if (TryReadParameter(token, out _, out var inlineValue))
            {
                if (inlineValue is null)
                    return false;

                token = TrimStaticQuotes(inlineValue);
            }

            const string fileSystemProvider = "FileSystem::";
            const string qualifiedFileSystemProvider = "Microsoft.PowerShell.Core\\FileSystem::";
            if (token.StartsWith(qualifiedFileSystemProvider, StringComparison.OrdinalIgnoreCase))
                token = token[qualifiedFileSystemProvider.Length..];
            if (token.StartsWith(fileSystemProvider, StringComparison.OrdinalIgnoreCase))
                token = token[fileSystemProvider.Length..];

            // "/" trimmed becomes "" but the original is clearly root
            if (token is "/" or "//" or "\\" or "\\\\")
                return true;

            var trimmed = token.TrimEnd('/', '\\');
            return trimmed is "~" or "$HOME" or "${HOME}"
                or "$env:USERPROFILE" or "${env:USERPROFILE}"
                || IsWindowsDriveRoot(token)
                || IsUncShareRoot(token)
                || string.Equals(trimmed, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAuthoredHomeVariable(string token)
        {
            var trimmed = TrimStaticQuotes(LegacyShellTextScan.TrimShellPunctuation(token))
                .TrimEnd('/', '\\');
            return trimmed is "$HOME" or "${HOME}"
                or "$env:USERPROFILE" or "${env:USERPROFILE}";
        }

        private static bool IsExplicitFalse(DenyToken fact)
        {
            return fact.IsKnown
                   && ShellCommandPolicy.IsExactlyAuthoredFalse(
                       fact.Value,
                       fact.AuthoredValue);
        }

        private static bool IsWindowsDriveRoot(string token)
        {
            if (token.Length < 3
                || !char.IsAsciiLetter(token[0])
                || token[1] != ':'
                || token[2] is not ('\\' or '/'))
            {
                return false;
            }

            return token.AsSpan(2).IndexOfAnyExcept('\\', '/') < 0;
        }

        private static bool IsUncShareRoot(string token)
        {
            if (token.Length < 5
                || token[0] is not ('\\' or '/')
                || token[1] is not ('\\' or '/'))
            {
                return false;
            }

            var segments = token[2..]
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 2;
        }
    }

    private static bool TryReadParameter(
        string token,
        out string parameterName,
        out string? inlineValue)
    {
        parameterName = string.Empty;
        inlineValue = null;
        if (token.Length < 2 || token[0] != '-')
            return false;

        var body = token.AsSpan(1);
        var separator = body.IndexOfAny(':', '=');
        var name = separator < 0 ? body : body[..separator];
        if (name.IsEmpty)
            return false;

        parameterName = name.ToString();
        if (separator >= 0 && separator < body.Length - 1)
            inlineValue = body[(separator + 1)..].ToString();

        return true;
    }

    private static bool IsParameterAbbreviation(string candidate, string parameterName)
        => candidate.Length > 0
           && parameterName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);

    private static bool IsExactlyAuthoredFalse(string value, string authoredValue)
    {
        if (!TryReadParameter(
                LegacyShellTextScan.TrimShellPunctuation(value),
                out _,
                out var decodedValue)
            || !IsBooleanFalse(decodedValue)
            || !TryReadParameter(
                LegacyShellTextScan.TrimShellPunctuation(authoredValue),
                out _,
                out var rawValue))
        {
            return false;
        }

        return string.Equals(rawValue, "$false", StringComparison.OrdinalIgnoreCase)
               || string.Equals(rawValue, "${false}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBooleanFalse(string? value)
        => value is not null
           && (TrimStaticQuotes(value).Equals("$false", StringComparison.OrdinalIgnoreCase)
               || TrimStaticQuotes(value).Equals("${false}", StringComparison.OrdinalIgnoreCase));

    private static string TrimStaticQuotes(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length >= 2
            && trimmed[0] is '\'' or '"'
            && trimmed[^1] == trimmed[0])
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    /// <summary>
    /// Verb-chain match with optional argFlags and firstPath refinements.
    /// Used when an operator override rule combines a structured verb match
    /// with additional constraints (e.g. "deny rm -rf when targeting root").
    /// </summary>
    internal sealed record RefinedVerbChainDenyPattern(
        IReadOnlyList<string> VerbChain,
        IReadOnlyList<string>? ArgFlags,
        PathConstraint? FirstPath,
        string Reason,
        DenyCategory Category) : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<DenyToken> tokens)
        {
            if (tokens.Count < VerbChain.Count)
                return false;

            for (var i = 0; i < VerbChain.Count; i++)
            {
                if (!tokens[i].IsKnown)
                    return false;

                var tokenVerb = LegacyShellTextScan.TrimShellPunctuation(tokens[i].Value);
                if (!string.Equals(tokenVerb, VerbChain[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (ArgFlags is { Count: > 0 } && !AnyFlagPresent(tokens, ArgFlags))
                return false;

            if (FirstPath is not null && !FirstNonFlagMatchesConstraint(tokens, VerbChain.Count, FirstPath))
                return false;

            return true;
        }

        private static bool AnyFlagPresent(IReadOnlyList<DenyToken> tokens, IReadOnlyList<string> requiredFlags)
        {
            // The flag is "present" if it appears as a standalone token OR if a
            // short combined flag token contains all requested short flag chars
            // (e.g. argFlag '-rf' matches token '-rfv').
            foreach (var required in requiredFlags)
            {
                if (TokensContainFlag(tokens, required))
                    return true;
            }

            return false;
        }

        private static bool TokensContainFlag(IReadOnlyList<DenyToken> tokens, string required)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].IsKnown)
                    continue;

                var token = tokens[i].Value;
                if (string.Equals(token, required, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Combined short flags: '-rf' present if any combined token
                // starts with '-' (single dash) and contains every short char.
                if (required.Length >= 2 && required[0] == '-' && required[1] != '-'
                    && token.Length >= 2 && token[0] == '-' && token[1] != '-')
                {
                    var requiredChars = required[1..];
                    var tokenChars = token[1..];
                    var allPresent = true;
                    foreach (var c in requiredChars)
                    {
                        if (!tokenChars.Contains(c, StringComparison.Ordinal))
                        {
                            allPresent = false;
                            break;
                        }
                    }

                    if (allPresent)
                        return true;
                }
            }

            return false;
        }

        private static bool FirstNonFlagMatchesConstraint(
            IReadOnlyList<DenyToken> tokens,
            int verbChainCount,
            PathConstraint constraint)
        {
            if (constraint.OneOf is not { Count: > 0 })
                return false;

            // Skip past the verb-chain tokens already consumed by the
            // prefix match in Matches(), then over any flag tokens; the
            // first remaining positional argument is the candidate path.
            // A bug landed when this was a static helper that assumed a
            // single-token verb chain — multi-token chains like
            // `git push` would treat `push` as the candidate path and
            // silently miss firstPath deny rules.
            for (var i = verbChainCount; i < tokens.Count; i++)
            {
                if (!tokens[i].IsKnown)
                    return false;

                var token = tokens[i].Value;
                if (token.StartsWith('-'))
                    continue;

                var normalized = NormalizePathToken(token);
                foreach (var candidate in constraint.OneOf)
                {
                    var normalizedCandidate = NormalizePathToken(candidate);
                    if (string.Equals(normalized, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // First non-flag positional arg checked; further args don't
                // satisfy "first path" semantics.
                return false;
            }

            return false;
        }

        private static string NormalizePathToken(string token)
            => PathUtility.ExpandHome(token).TrimEnd('/', '\\');
    }

}
