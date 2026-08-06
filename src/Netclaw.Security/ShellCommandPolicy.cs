// -----------------------------------------------------------------------
// <copyright file="ShellCommandPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    private readonly ShellExecutionEnvironment _environment;
    private readonly IReadOnlyList<DenyPattern> _denyPatterns;
    private readonly IReadOnlyList<RawStringPattern> _rawStringPatterns;

    public ShellExecutionEnvironment ExecutionEnvironment => _environment;

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
        ShellExecutionEnvironment environment,
        IEnumerable<string>? additionalDenyPatterns,
        IEnumerable<HardDenyRule>? overrideRules)
    {
        _environment = environment;
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
    /// Nested command strings are flattened by the canonical parser.
    /// </summary>
    public ShellCommandDecision Evaluate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ShellCommandDecision.Allow();

        // Check raw-string patterns first (fork bombs, etc.) before splitting,
        // since compound-command splitting destroys the original formatting.
        var rawDecision = EvaluateRawString(command);
        if (!rawDecision.Allowed)
            return rawDecision;

        return EvaluateAnalysis(command, _environment.Analyze(command));
    }

    /// <summary>
    /// Evaluates a Bash command with the production Bash analysis on every host.
    /// </summary>
    internal ShellCommandDecision EvaluateBash(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ShellCommandDecision.Allow();

        var rawDecision = EvaluateRawString(command);
        return rawDecision.Allowed
            ? EvaluateAnalysis(command, ShellCommandAnalyzer.Bash.Analyze(command))
            : rawDecision;
    }

    private ShellCommandDecision EvaluateAnalysis(string command, ShellCommandAnalysis analysis)
    {
        if (analysis.Failure == ShellAnalysisFailure.UnsupportedShellWrapper)
        {
            foreach (var clause in analysis.Clauses)
            {
                var clauseDecision = EvaluateClause(clause);
                if (!clauseDecision.Allowed)
                    return clauseDecision;
            }

            return ShellCommandDecision.Deny(
                "Command uses an unsupported shell wrapper",
                DenyCategory.CustomDeny);
        }

        if (analysis.Failure == ShellAnalysisFailure.Unresolved || analysis.Clauses.Count == 0)
            return EvaluateLegacySegments(command);

        foreach (var clause in analysis.Clauses)
        {
            var decision = EvaluateClause(clause);
            if (!decision.Allowed)
                return decision;
        }

        if (analysis.HasDynamicSyntax)
            return EvaluateLegacySegments(command);

        return ShellCommandDecision.Allow();
    }

    private ShellCommandDecision EvaluateLegacySegments(string command)
    {
        // The approval matcher does not persist unresolved syntax. Keep the
        // legacy scan here so known deny forms still fail at this boundary.
        foreach (var segment in ShellTokenizer.GetAllCommandSegments(command))
        {
            var decision = EvaluateSegment(segment);
            if (!decision.Allowed)
                return decision;
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
        var tokens = new List<string>(clause.Verb.Tokens.Count + clause.Args.Count + clause.Redirects.Count);
        if (!string.IsNullOrWhiteSpace(clause.Verb.CanonicalVerb))
            tokens.Add(_environment.Grammar == ShellGrammar.PowerShell
                ? ShellExecutionEnvironment.NormalizePowerShellVerb(clause.Verb.CanonicalVerb)
                : clause.Verb.CanonicalVerb);
        else
        {
            if (_environment.Grammar == ShellGrammar.PowerShell && clause.Verb.Tokens.Count > 0)
            {
                tokens.Add(ShellExecutionEnvironment.NormalizePowerShellVerb(clause.Verb.Tokens[0]));
                tokens.AddRange(clause.Verb.Tokens.Skip(1));
            }
            else
            {
                tokens.AddRange(clause.Verb.Tokens);
            }
        }
        tokens.AddRange(clause.Args
            .Where(static arg => !arg.IsCwdAttribution)
            .Select(static arg => arg.Raw));
        tokens.AddRange(clause.Redirects
            .Where(static redirect => !string.IsNullOrEmpty(redirect.Target))
            .Select(static redirect => redirect.Target));

        if (tokens.Count == 0)
            return ShellCommandDecision.Allow();

        foreach (var pattern in _denyPatterns)
        {
            if (pattern.Matches(tokens))
                return ShellCommandDecision.Deny(pattern.Reason, pattern.Category);
        }

        return ShellCommandDecision.Allow();
    }

    private ShellCommandDecision EvaluateSegment(string segment)
    {
        var tokens = ShellTokenizer.Tokenize(segment).ToList();
        if (tokens.Count == 0)
            return ShellCommandDecision.Allow();

        // The PowerShell parser rejects unsupported module-qualified cmdlets.
        // The legacy scan must still apply hard denies to their cmdlet names.
        if (_environment.Grammar == ShellGrammar.PowerShell)
            tokens[0] = ShellExecutionEnvironment.NormalizePowerShellVerb(tokens[0]);

        foreach (var pattern in _denyPatterns)
        {
            if (pattern.Matches(tokens))
                return ShellCommandDecision.Deny(pattern.Reason, pattern.Category);
        }

        return ShellCommandDecision.Allow();
    }

    private static DenyPattern? ParseDenyPattern(string raw)
    {
        var tokens = ShellTokenizer.Tokenize(raw).ToList();
        if (tokens.Count == 0)
            return null;

        return new VerbChainDenyPattern(tokens, raw, DenyCategory.CustomDeny);
    }

    private static bool IsPowerShellParameterAbbreviation(string token, string parameter)
    {
        if (token.Length < 2 || token[0] != '-')
            return false;

        var name = token[1..];
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
            name = name[..colon];

        return name.Length > 0 && parameter.StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    // ── Default deny patterns ──

    private static readonly IReadOnlyList<DenyPattern> DefaultDenyPatterns =
    [
        // Self-destruction: killing the netclaw daemon
        new VerbChainDenyPattern(["netclaw", "daemon", "stop"], "Cannot stop the daemon from within a session", DenyCategory.SelfDestructive),
        new VerbChainDenyPattern(["netclaw", "daemon", "kill"], "Cannot kill the daemon from within a session", DenyCategory.SelfDestructive),
        new VerbChainDenyPattern(["systemctl", "stop", "netclaw"], "Cannot stop the netclaw service", DenyCategory.SelfDestructive),
        new VerbChainDenyPattern(["systemctl", "kill", "netclaw"], "Cannot kill the netclaw service", DenyCategory.SelfDestructive),

        // Process killing patterns targeting netclaw
        new ProcessKillDenyPattern("Cannot kill processes from within a session", DenyCategory.SelfDestructive),

        // Privilege escalation: the agent must never elevate privileges.
        // If it needs elevated access, the daemon should run as a user with those permissions.
        new PrivilegeEscalationDenyPattern("Cannot escalate privileges from within a session", DenyCategory.PrivilegeEscalation),

        // System-destructive: rm -rf on root or home
        new RmRfRootDenyPattern("Cannot remove root or home directories", DenyCategory.SystemDestructive),
        new PowerShellRemoveItemRootDenyPattern("Cannot remove root or home directories", DenyCategory.SystemDestructive),

        // PowerShell elevation through Start-Process is the native equivalent
        // of sudo/doas and must remain categorically unavailable.
        new PowerShellRunAsDenyPattern("Cannot escalate privileges from within a session", DenyCategory.PrivilegeEscalation),

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

    internal abstract record DenyPattern(string Reason, DenyCategory Category)
    {
        public abstract bool Matches(IReadOnlyList<string> tokens);
    }

    /// <summary>
    /// Matches when the command starts with the specified verb chain (case-insensitive).
    /// </summary>
    internal sealed record VerbChainDenyPattern(
        IReadOnlyList<string> VerbChain,
        string Reason,
        DenyCategory Category) : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count < VerbChain.Count)
                return false;

            for (var i = 0; i < VerbChain.Count; i++)
            {
                var tokenVerb = ShellTokenizer.TrimShellPunctuation(tokens[i]);
                if (!string.Equals(tokenVerb, VerbChain[i], StringComparison.OrdinalIgnoreCase))
                    return false;
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
        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0)
                return false;

            var verb = ShellTokenizer.TrimShellPunctuation(tokens[0]);
            return verb.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Matches kill/killall/pkill commands. These are categorically denied because
    /// the agent could target the daemon process or other critical processes.
    /// </summary>
    internal sealed record ProcessKillDenyPattern(string Reason, DenyCategory Category)
        : DenyPattern(Reason, Category)
    {
        private static readonly HashSet<string> KillVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "kill", "killall", "pkill", "Stop-Process"
        };

        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0)
                return false;

            var verb = ShellTokenizer.TrimShellPunctuation(tokens[0]);
            return KillVerbs.Contains(verb);
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

        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0)
                return false;

            var verb = ShellTokenizer.TrimShellPunctuation(tokens[0]);
            return EscalationVerbs.Contains(verb);
        }
    }

    /// <summary>
    /// Matches rm -rf targeting root (/) or home (~/ or $HOME).
    /// </summary>
    internal sealed record RmRfRootDenyPattern(string Reason, DenyCategory Category)
        : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count < 2)
                return false;

            var verb = ShellTokenizer.TrimShellPunctuation(tokens[0]);
            if (!string.Equals(verb, "rm", StringComparison.OrdinalIgnoreCase))
                return false;

            var hasRecursive = false;
            var hasForce = false;
            var hasDangerousTarget = false;

            for (var i = 1; i < tokens.Count; i++)
            {
                var token = tokens[i];

                // Check for -rf, -fr, --recursive + --force, etc.
                if (token.StartsWith('-') && !token.StartsWith("--", StringComparison.Ordinal))
                {
                    if (token.Contains('r', StringComparison.Ordinal) || token.Contains('R', StringComparison.Ordinal))
                        hasRecursive = true;

                    if (token.Contains('f', StringComparison.Ordinal))
                        hasForce = true;
                }
                else if (token == "--recursive")
                {
                    hasRecursive = true;
                }
                else if (token == "--force")
                {
                    hasForce = true;
                }

                // Check for dangerous targets
                if (IsDangerousRmTarget(token))
                    hasDangerousTarget = true;
            }

            return hasRecursive && hasForce && hasDangerousTarget;
        }

        private static bool IsDangerousRmTarget(string token)
        {
            // "/" trimmed becomes "" but the original is clearly root
            if (token is "/" or "//")
                return true;

            var trimmed = token.TrimEnd('/', '\\');
            return trimmed is "~" or "$HOME" or "${HOME}"
                || string.Equals(trimmed, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed record PowerShellRemoveItemRootDenyPattern(string Reason, DenyCategory Category)
        : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count < 2
                || !string.Equals(tokens[0], "Remove-Item", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var recursive = tokens.Any(static token =>
                IsPowerShellParameterAbbreviation(token, "Recurse"));
            var force = tokens.Any(static token =>
                IsPowerShellParameterAbbreviation(token, "Force"));
            var dangerousTarget = tokens.Skip(1).Any(IsDangerousPowerShellTarget);
            return recursive && force && dangerousTarget;
        }

        private static bool IsDangerousPowerShellTarget(string token)
        {
            var unquoted = token.Trim('"', '\'').TrimEnd('/', '\\');
            if (unquoted.Length == 2 && char.IsAsciiLetter(unquoted[0]) && unquoted[1] == ':')
                return true;

            return unquoted.Equals("~", StringComparison.OrdinalIgnoreCase)
                   || unquoted.Equals("$HOME", StringComparison.OrdinalIgnoreCase)
                   || unquoted.Equals("${HOME}", StringComparison.OrdinalIgnoreCase)
                   || unquoted.Equals("$env:USERPROFILE", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed record PowerShellRunAsDenyPattern(string Reason, DenyCategory Category)
        : DenyPattern(Reason, Category)
    {
        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count < 2
                || !string.Equals(tokens[0], "Start-Process", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var i = 1; i < tokens.Count; i++)
            {
                if (IsPowerShellParameterAbbreviation(tokens[i], "Verb")
                    && i + 1 < tokens.Count
                    && tokens[i + 1].Trim('"', '\'').Equals("RunAs", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var colon = tokens[i].IndexOf(':', StringComparison.Ordinal);
                if (colon > 1
                    && IsPowerShellParameterAbbreviation(tokens[i], "Verb")
                    && tokens[i][(colon + 1)..].Trim('"', '\'').Equals("RunAs", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
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
        public override bool Matches(IReadOnlyList<string> tokens)
        {
            if (tokens.Count < VerbChain.Count)
                return false;

            for (var i = 0; i < VerbChain.Count; i++)
            {
                var tokenVerb = ShellTokenizer.TrimShellPunctuation(tokens[i]);
                if (!string.Equals(tokenVerb, VerbChain[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (ArgFlags is { Count: > 0 } && !AnyFlagPresent(tokens, ArgFlags))
                return false;

            if (FirstPath is not null && !FirstNonFlagMatchesConstraint(tokens, VerbChain.Count, FirstPath))
                return false;

            return true;
        }

        private static bool AnyFlagPresent(IReadOnlyList<string> tokens, IReadOnlyList<string> requiredFlags)
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

        private static bool TokensContainFlag(IReadOnlyList<string> tokens, string required)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
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
            IReadOnlyList<string> tokens,
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
                var token = tokens[i];
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
