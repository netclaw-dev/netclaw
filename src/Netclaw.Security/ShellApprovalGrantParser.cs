// -----------------------------------------------------------------------
// <copyright file="ShellApprovalGrantParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Configuration;
using ShellSyntaxTree;
using System.Diagnostics.CodeAnalysis;

namespace Netclaw.Security;

/// <summary>
/// Converts one static phrase into one reusable shell approval grant. The CLI
/// supplies operator-authored phrases. A compatibility API can supply one
/// legacy pattern. This type does not analyze runtime command strings.
/// </summary>
public static class ShellApprovalGrantParser
{
    /// <summary>
    /// Parses one exact static grant phrase. This method does not accept all
    /// legal shell spellings or analyze a runtime command string. Extra source
    /// text fails instead of broadening the stored token prefix.
    /// </summary>
    public static bool TryCreateTokenPrefix(
        ApprovalShell shell,
        string source,
        [NotNullWhen(true)] out ApprovalEntry? entry,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(source);
        entry = null;
        error = string.Empty;
        try
        {
            if (shell == ApprovalShell.PowerShell)
            {
                var powerShell7 = ParsePowerShell(source, PwshDialect.PowerShell7);
                var windowsPowerShell = ParsePowerShell(source, PwshDialect.WindowsPowerShell51);
                var hasPreferredEntry = TryCreateTokenPrefixCore(
                    ApprovalShell.PowerShell,
                    source,
                    powerShell7,
                    out var preferredEntry,
                    out _);
                var hasFallbackEntry = TryCreateTokenPrefixCore(
                    ApprovalShell.PowerShell,
                    source,
                    windowsPowerShell,
                    out var fallbackEntry,
                    out _);
                if (hasPreferredEntry && preferredEntry is not null)
                {
                    entry = preferredEntry;
                    return true;
                }

                if (hasFallbackEntry && fallbackEntry is not null)
                {
                    entry = fallbackEntry;
                    return true;
                }

                error = "The PowerShell phrase must have one canonical form in a supported dialect.";
                return false;
            }

            if (shell != ApprovalShell.Bash)
            {
                error = "The shell identity is not supported.";
                return false;
            }

            return TryCreateTokenPrefixCore(
                ApprovalShell.Bash,
                source,
                ParseBash(source),
                out entry,
                out error);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error = "The shell phrase could not be parsed.";
            return false;
        }
    }

    /// <summary>
    /// Parses one grant phrase with the daemon's resolved native grammar and
    /// PowerShell dialect.
    /// </summary>
    public static bool TryCreateTokenPrefix(
        ShellExecutionEnvironment environment,
        string source,
        [NotNullWhen(true)] out ApprovalEntry? entry,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(source);
        entry = null;
        error = string.Empty;
        try
        {
            var shell = environment.Grammar switch
            {
                ShellGrammar.Bash => ApprovalShell.Bash,
                ShellGrammar.PowerShell => ApprovalShell.PowerShell,
                _ => throw new InvalidOperationException("The shell grammar is not supported."),
            };
            return TryCreateTokenPrefixCore(
                shell,
                source,
                environment.Parse(source),
                out entry,
                out error);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error = "The shell phrase could not be parsed.";
            return false;
        }
    }

    private static bool TryCreateTokenPrefixCore(
        ApprovalShell shell,
        string source,
        ParsedCommand parsed,
        [NotNullWhen(true)] out ApprovalEntry? entry,
        out string error)
    {
        entry = null;
        error = string.Empty;
        if (parsed.IsUnparseable ||
            parsed.Commands.Count != 1 ||
            parsed.Syntax.Statements.Count != 1 ||
            parsed.Syntax.Statements[0] is not SimpleCommandSyntax simple)
        {
            error = "The shell phrase must contain one complete static command.";
            return false;
        }

        var occurrence = parsed.Commands[0];
        var clause = occurrence.Clause;
        if (!occurrence.IsComplete ||
            occurrence.ImmediateRole != CommandOccurrenceRole.Ordinary ||
            clause.Operator != CompoundOperator.None ||
            clause.IsSubshell ||
            clause.IsCommandStringWrapped ||
            clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count == 0 ||
            clause.Args.Count != 0 ||
            clause.Redirects.Count != 0 ||
            simple.Substitutions.Count != 0 ||
            simple.ExecutionRegions.Count != 0 ||
            clause.Elements.Count != clause.Verb.Tokens.Count ||
            clause.Elements.Any(static element =>
                element.Role != ClauseElementRole.Verb || element.IsFlag))
        {
            error = "The shell phrase must have no argument, flag, assignment, redirect, or control effect.";
            return false;
        }

        var tokens = clause.Verb.Tokens.ToArray();
        if (clause.Verb.CanonicalVerb is { Length: > 0 } canonicalVerb)
        {
            tokens[0] = canonicalVerb;
        }

        var canonicalSource = string.Join(" ", tokens);
        if (!string.Equals(source, canonicalSource, StringComparison.Ordinal))
        {
            error = $"The shell phrase must equal its canonical form: {canonicalSource}";
            return false;
        }

        entry = ApprovalEntry.CreateTokenPrefix(shell, tokens);
        return true;
    }

    private static ParsedCommand ParseBash(string source) =>
        new BashParser(new BashParserOptions
        {
            InitialStateMode = BashInitialStateMode.Unknown,
        }).Parse(source);

    private static ParsedCommand ParsePowerShell(string source, PwshDialect dialect) =>
        new PwshParser(new PwshParserOptions
        {
            InitialStateMode = PwshInitialStateMode.Unknown,
            Dialect = dialect,
        }).Parse(source);
}
