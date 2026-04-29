// -----------------------------------------------------------------------
// <copyright file="ShellCommandPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Result of evaluating a shell command against the hard deny list.
/// </summary>
public sealed record ShellCommandDecision(bool Allowed, string? DenyReason = null, string? DenyCategory = null)
{
    public static ShellCommandDecision Allow() => new(true);

    public static ShellCommandDecision Deny(string reason, string category) => new(false, reason, category);
}

/// <summary>
/// Evaluates shell commands against a configurable hard deny list.
/// Denied commands are categorically blocked and cannot be approved.
/// Uses structural matching (tokenized verb + subcommand + flags),
/// not substring matching on the raw command string.
/// </summary>
public sealed class ShellCommandPolicy
{
    private readonly IReadOnlyList<DenyPattern> _denyPatterns;
    private readonly IReadOnlyList<RawStringPattern> _rawStringPatterns;

    public ShellCommandPolicy(IEnumerable<string>? additionalDenyPatterns = null)
    {
        var patterns = new List<DenyPattern>(DefaultDenyPatterns);
        if (additionalDenyPatterns is not null)
        {
            foreach (var pattern in additionalDenyPatterns)
            {
                var parsed = ParseDenyPattern(pattern);
                if (parsed is not null)
                    patterns.Add(parsed);
            }
        }

        _denyPatterns = patterns;
        _rawStringPatterns = DefaultRawStringPatterns;
    }

    /// <summary>
    /// Evaluates a shell command (possibly compound) against the deny list.
    /// If any segment of a compound command matches, the entire command is denied.
    /// Recursively scans bash -c / sh -c inner commands.
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

        var segments = ShellTokenizer.GetAllCommandSegments(command);
        foreach (var segment in segments)
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

    private ShellCommandDecision EvaluateSegment(string segment)
    {
        var tokens = ShellTokenizer.Tokenize(segment).ToList();
        if (tokens.Count == 0)
            return ShellCommandDecision.Allow();

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

        return new VerbChainDenyPattern(tokens, raw, "custom_deny");
    }

    // ── Default deny patterns ──

    private static readonly IReadOnlyList<DenyPattern> DefaultDenyPatterns =
    [
        // Self-destruction: killing the netclaw daemon
        new VerbChainDenyPattern(["netclaw", "daemon", "stop"], "Cannot stop the daemon from within a session", "self_destructive"),
        new VerbChainDenyPattern(["netclaw", "daemon", "kill"], "Cannot kill the daemon from within a session", "self_destructive"),
        new VerbChainDenyPattern(["systemctl", "stop", "netclaw"], "Cannot stop the netclaw service", "self_destructive"),
        new VerbChainDenyPattern(["systemctl", "kill", "netclaw"], "Cannot kill the netclaw service", "self_destructive"),

        // Process killing patterns targeting netclaw
        new ProcessKillDenyPattern("Cannot kill processes from within a session", "self_destructive"),

        // Privilege escalation: the agent must never elevate privileges.
        // If it needs elevated access, the daemon should run as a user with those permissions.
        new PrivilegeEscalationDenyPattern("Cannot escalate privileges from within a session", "privilege_escalation"),

        // System-destructive: rm -rf on root or home
        new RmRfRootDenyPattern("Cannot remove root or home directories", "system_destructive"),

        // Filesystem destruction (mkfs, mkfs.ext4, mkfs.xfs, etc.)
        new VerbPrefixDenyPattern("mkfs", "Cannot create filesystems", "system_destructive"),
    ];

    /// <summary>
    /// Patterns matched against the raw command string before tokenization.
    /// Used for patterns (like fork bombs) that don't tokenize cleanly.
    /// </summary>
    private static readonly IReadOnlyList<RawStringPattern> DefaultRawStringPatterns =
    [
        new(":(){ :|:& };:", "Fork bomb detected", "system_destructive"),
        new(":(){:|:&};:", "Fork bomb detected", "system_destructive"),
    ];

    internal sealed record RawStringPattern(string Pattern, string Reason, string Category);

    // ── Pattern types ──

    internal abstract record DenyPattern(string Reason, string Category)
    {
        public abstract bool Matches(IReadOnlyList<string> tokens);
    }

    /// <summary>
    /// Matches when the command starts with the specified verb chain (case-insensitive).
    /// </summary>
    internal sealed record VerbChainDenyPattern(
        IReadOnlyList<string> VerbChain,
        string Reason,
        string Category) : DenyPattern(Reason, Category)
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
        string Category) : DenyPattern(Reason, Category)
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
    internal sealed record ProcessKillDenyPattern(string Reason, string Category)
        : DenyPattern(Reason, Category)
    {
        private static readonly HashSet<string> KillVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "kill", "killall", "pkill"
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
    internal sealed record PrivilegeEscalationDenyPattern(string Reason, string Category)
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
    internal sealed record RmRfRootDenyPattern(string Reason, string Category)
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
                || string.Equals(trimmed, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    StringComparison.OrdinalIgnoreCase);
        }
    }

}
