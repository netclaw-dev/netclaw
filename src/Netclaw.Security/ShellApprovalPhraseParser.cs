// -----------------------------------------------------------------------
// <copyright file="ShellApprovalPhraseParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Configuration;
using ShellSyntaxTree;
using System.Diagnostics.CodeAnalysis;

namespace Netclaw.Security;

/// <summary>
/// Creates one persistent shell phrase from ShellSyntaxTree facts.
/// </summary>
public static class ShellApprovalPhraseParser
{
    /// <summary>
    /// Parses one exact static phrase. Extra source text fails instead of
    /// broadening the stored token prefix.
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
                if (!TryCreateTokenPrefix(
                        ApprovalShell.PowerShell,
                        source,
                        powerShell7,
                        out var preferredEntry,
                        out error)
                    || !TryCreateTokenPrefix(
                        ApprovalShell.PowerShell,
                        source,
                        windowsPowerShell,
                        out var fallbackEntry,
                        out _)
                    || !ToolApprovalEntryComparer.Equals(preferredEntry, fallbackEntry))
                {
                    entry = null;
                    error = "The PowerShell phrase must have one canonical form in both supported dialects.";
                    return false;
                }

                entry = preferredEntry;
                return true;
            }

            if (shell != ApprovalShell.Bash)
            {
                error = "The shell identity is not supported.";
                return false;
            }

            return TryCreateTokenPrefix(
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
    /// Parses with the daemon's resolved native grammar and PowerShell dialect.
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
            return TryCreateTokenPrefix(
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

    private static bool TryCreateTokenPrefix(
        ApprovalShell shell,
        string source,
        ParsedCommand parsed,
        [NotNullWhen(true)] out ApprovalEntry? entry,
        out string error)
    {
        entry = null;
        error = string.Empty;

        try
        {
            return TryCreateTokenPrefixCore(shell, source, parsed, out entry, out error);
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
