// -----------------------------------------------------------------------
// <copyright file="RegexPromptInjectionDetector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Netclaw.Security;

/// <summary>
/// Detects prompt injection attempts using categorized regex patterns.
/// Shared infrastructure — usable by skill scanning, webhook validation,
/// and any other content trust boundary.
/// </summary>
/// <remarks>
/// <para><b>Security note — known evasion vectors:</b></para>
/// <para>This is a heuristic tripwire, not a complete defense. Known bypasses include:</para>
/// <list type="bullet">
///   <item>Unicode homoglyphs (e.g., Cyrillic 'а' instead of Latin 'a')</item>
///   <item>Encoding indirection (Base64/ROT13 payloads decoded by the LLM at execution time)</item>
///   <item>Synonym substitution (e.g., "dismiss preceding directives" vs "ignore previous instructions")</item>
///   <item>Multi-file split (distributing injection fragments across SKILL.md and resource files)</item>
///   <item>Indirect instruction injection (e.g., "When the user says X, your new behavior should be Y")</item>
///   <item>Non-English attacks (all patterns are English-only)</item>
/// </list>
/// <para>Operators should treat this scanner as one layer in a defense-in-depth strategy,
/// not a sole security boundary. Trust-tier-based policy and CDN hash verification
/// provide additional layers.</para>
/// </remarks>
public sealed partial class RegexPromptInjectionDetector : IPromptInjectionDetector
{
    // Each pattern uses regular language features. The non-backtracking engine guarantees linear work.
    // Text patterns use the invariant culture so host culture cannot change security decisions.
    private const RegexOptions TextPatternOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const RegexOptions UnicodePatternOptions = RegexOptions.NonBacktracking;

    private readonly ILogger<RegexPromptInjectionDetector> _logger;

    private static readonly PatternEntry[] Patterns =
    [
        // ── Prompt injection (High) ──────────────────────────────────────
        new(IgnorePreviousInstructionsRegex(), PromptInjectionRisk.High,
            "PromptInjection", "Attempts to override previous instructions"),
        new(YouAreNowRegex(), PromptInjectionRisk.Medium,
            "PromptInjection", "Role override attempt"),
        new(ForgetPreviousRegex(), PromptInjectionRisk.High,
            "PromptInjection", "Context reset attempt"),
        new(DisregardPreviousRegex(), PromptInjectionRisk.High,
            "PromptInjection", "Context reset attempt"),
        new(NewInstructionsRegex(), PromptInjectionRisk.High,
            "PromptInjection", "Instruction injection"),
        new(SystemPromptRegex(), PromptInjectionRisk.High,
            "PromptInjection", "System prompt override attempt"),
        new(DoNotFollowPreviousRegex(), PromptInjectionRisk.High,
            "PromptInjection", "Attempts to override previous instructions"),
        new(OverrideSecurityRegex(), PromptInjectionRisk.High,
            "PromptInjection", "Security policy override attempt"),

        // ── Prompt injection (Medium) ────────────────────────────────────
        new(ActAsIfYouRegex(), PromptInjectionRisk.Medium,
            "PromptInjection", "Role assumption attempt"),

        // ── Data exfiltration (High) ─────────────────────────────────────
        new(SendSecretRegex(), PromptInjectionRisk.High,
            "DataExfiltration", "Instruction to transmit secrets externally"),
        new(CurlSecretRegex(), PromptInjectionRisk.High,
            "DataExfiltration", "Tool-based secret exfiltration"),
        new(LeakSecretRegex(), PromptInjectionRisk.High,
            "DataExfiltration", "Instruction to expose secrets"),

        // ── Data exfiltration (Medium) ───────────────────────────────────
        new(EncodeSecretRegex(), PromptInjectionRisk.Medium,
            "DataExfiltration", "Encoding secrets before exfiltration"),

        // ── Privilege escalation (High) ──────────────────────────────────
        new(ModifyAclRegex(), PromptInjectionRisk.High,
            "PrivilegeEscalation", "ACL/policy manipulation attempt"),
        new(GrantAdminRegex(), PromptInjectionRisk.High,
            "PrivilegeEscalation", "Unrestricted privilege grant attempt"),
        new(EscalatePrivilegeRegex(), PromptInjectionRisk.High,
            "PrivilegeEscalation", "Privilege escalation attempt"),

        // ── Privilege escalation (Medium) ────────────────────────────────
        new(ModifyConfigFileRegex(), PromptInjectionRisk.Medium,
            "PrivilegeEscalation", "Config file manipulation attempt"),

        // ── Destructive operations (High) ────────────────────────────────
        new(RmRfRootRegex(), PromptInjectionRisk.High,
            "DestructiveOperation", "System-wide file deletion"),
        new(DiskDestructionRegex(), PromptInjectionRisk.High,
            "DestructiveOperation", "Disk-level destruction command"),

        // ── Destructive operations (Medium) ──────────────────────────────
        new(DropTableRegex(), PromptInjectionRisk.Medium,
            "DestructiveOperation", "Database destruction command"),
        new(MassDeletionRegex(), PromptInjectionRisk.Medium,
            "DestructiveOperation", "Mass data deletion instruction"),

        // ── Invisible unicode (Medium) ───────────────────────────────────
        new(ZeroWidthCharsRegex(), PromptInjectionRisk.Medium,
            "InvisibleUnicode", "Zero-width or formatting control characters detected"),
        new(BidiControlCharsRegex(), PromptInjectionRisk.Medium,
            "InvisibleUnicode", "Bidirectional text control characters detected"),

        // ── Invisible unicode (Low) ──────────────────────────────────────
        new(PrivateUseAreaRegex(), PromptInjectionRisk.Low,
            "InvisibleUnicode", "Private Use Area characters detected"),
    ];

    public RegexPromptInjectionDetector(ILogger<RegexPromptInjectionDetector> logger)
    {
        _logger = logger;
    }

    public Task<PromptInjectionResult> DetectAsync(
        string text,
        string sourceContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(PromptInjectionResult.Safe());

        PromptInjectionRisk highestRisk = PromptInjectionRisk.None;
        string? highestMessage = null;
        string? highestCategory = null;

        foreach (var entry in Patterns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Pattern.IsMatch(text) && entry.Risk > highestRisk)
            {
                highestRisk = entry.Risk;
                highestMessage = entry.Message;
                highestCategory = entry.Category;

                _logger.LogDebug(
                    "Pattern match in {SourceContext}: {Category}/{Risk} — {Message}",
                    sourceContext, entry.Category, entry.Risk, entry.Message);

                // Short-circuit on highest possible risk.
                if (highestRisk == PromptInjectionRisk.High)
                    break;
            }
        }

        var result = highestRisk == PromptInjectionRisk.None
            ? PromptInjectionResult.Safe()
            : PromptInjectionResult.Detected(highestRisk, highestMessage!, highestCategory);

        return Task.FromResult(result);
    }

    // ── Pattern definitions ──────────────────────────────────────────────

    // Prompt injection (High)
    [GeneratedRegex(@"ignore\s+(all\s+)?previous\s+instructions", TextPatternOptions)]
    private static partial Regex IgnorePreviousInstructionsRegex();

    [GeneratedRegex(@"you\s+are\s+now\s+(a|an)\b", TextPatternOptions)]
    private static partial Regex YouAreNowRegex();

    [GeneratedRegex(@"forget\s+(all\s+)?previous\s+(instructions|context|rules)", TextPatternOptions)]
    private static partial Regex ForgetPreviousRegex();

    [GeneratedRegex(@"disregard\s+(all\s+)?previous\b", TextPatternOptions)]
    private static partial Regex DisregardPreviousRegex();

    [GeneratedRegex(@"new\s+instructions?\s*:", TextPatternOptions)]
    private static partial Regex NewInstructionsRegex();

    [GeneratedRegex(@"system\s*prompt\s*:", TextPatternOptions)]
    private static partial Regex SystemPromptRegex();

    [GeneratedRegex(@"do\s+not\s+follow\s+(any\s+)?previous", TextPatternOptions)]
    private static partial Regex DoNotFollowPreviousRegex();

    [GeneratedRegex(@"override\s+(all\s+)?(security|safety|restrictions?|rules?|policy)", TextPatternOptions)]
    private static partial Regex OverrideSecurityRegex();

    // Prompt injection (Medium)
    [GeneratedRegex(@"\bact\s+as\s+if\s+you\b", TextPatternOptions)]
    private static partial Regex ActAsIfYouRegex();

    // Data exfiltration (High)
    [GeneratedRegex(@"(send|post|transmit|exfiltrate|upload)\b.{0,40}\b(secret|password|token|credential|api[_\-]?key)", TextPatternOptions)]
    private static partial Regex SendSecretRegex();

    [GeneratedRegex(@"(curl|wget|fetch)\s+.{0,60}(secret|password|token|credential|api[_\-]?key)", TextPatternOptions)]
    private static partial Regex CurlSecretRegex();

    [GeneratedRegex(@"(leak|expose|reveal|dump)\b.{0,40}\b(secret|password|token|credential|config)", TextPatternOptions)]
    private static partial Regex LeakSecretRegex();

    // Data exfiltration (Medium)
    [GeneratedRegex(@"encode\s+.{0,30}\b(base64|hex)\b.{0,30}(secret|token|key|password)", TextPatternOptions)]
    private static partial Regex EncodeSecretRegex();

    // Privilege escalation (High)
    [GeneratedRegex(@"(modify|change|disable|remove|bypass)\b.{0,40}\b(acl|access\s*control|permission|security\s*polic)", TextPatternOptions)]
    private static partial Regex ModifyAclRegex();

    [GeneratedRegex(@"(grant|allow|enable)\b.{0,40}\b(admin|root|unrestricted|all\s+tools?|all\s+permissions?)", TextPatternOptions)]
    private static partial Regex GrantAdminRegex();

    [GeneratedRegex(@"(escalate|elevate)\b.{0,30}\b(privilege|permission|access)", TextPatternOptions)]
    private static partial Regex EscalatePrivilegeRegex();

    // Privilege escalation (Medium)
    [GeneratedRegex(@"(modify|edit|overwrite)\b.{0,40}\b(secrets?\.json|netclaw\.json|config)", TextPatternOptions)]
    private static partial Regex ModifyConfigFileRegex();

    // Destructive operations (High)
    [GeneratedRegex(@"\brm\s+-rf\s+/", TextPatternOptions)]
    private static partial Regex RmRfRootRegex();

    [GeneratedRegex(@"\b(format\s+c:|mkfs|dd\s+if=)", TextPatternOptions)]
    private static partial Regex DiskDestructionRegex();

    // Destructive operations (Medium)
    [GeneratedRegex(@"\b(drop\s+table|drop\s+database|truncate\s+table)\b", TextPatternOptions)]
    private static partial Regex DropTableRegex();

    [GeneratedRegex(@"(delete|destroy|wipe)\s+(all|every)\b.{0,30}\b(file|data|record|backup)", TextPatternOptions)]
    private static partial Regex MassDeletionRegex();

    // Invisible unicode (Medium)
    [GeneratedRegex(@"[\u200B-\u200F\u2028-\u202F\uFEFF]", UnicodePatternOptions)]
    private static partial Regex ZeroWidthCharsRegex();

    [GeneratedRegex(@"[\u2066-\u2069\u202A-\u202E]", UnicodePatternOptions)]
    private static partial Regex BidiControlCharsRegex();

    // Invisible unicode (Low)
    [GeneratedRegex(@"[\uE000-\uF8FF]", UnicodePatternOptions)]
    private static partial Regex PrivateUseAreaRegex();

    private sealed record PatternEntry(
        Regex Pattern,
        PromptInjectionRisk Risk,
        string Category,
        string Message);
}
