// -----------------------------------------------------------------------
// <copyright file="CliArgsParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli;

public enum CliParseKind
{
    NoArgs,
    Help,
    Version,
    Known,
    Unknown,
}

public record CliParseResult(CliParseKind Kind, string? Mode = null)
{
    public static readonly CliParseResult NoArgs = new(CliParseKind.NoArgs);
    public static readonly CliParseResult Help = new(CliParseKind.Help);
    public static readonly CliParseResult Version = new(CliParseKind.Version);
}

/// <summary>Classifies top-level command-line arguments for the netclaw CLI.</summary>
public static class CliArgsParser
{
    /// <summary>
    /// The set of top-level commands the CLI can dispatch. Must stay in sync with
    /// the mode handlers in Program.cs. Exposed publicly so tests can assert completeness.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "chat", "sessions", "init", "doctor", "status", "stats",
        "daemon", "mcp", "provider", "model", "reminder", "memory",
        "secrets", "config", "update", "pair", "skill", "webhooks",
        "approvals",
    };

    /// <summary>Returns <c>true</c> if the token is a help flag (<c>help</c>, <c>-h</c>, <c>--help</c>).</summary>
    public static bool IsHelpToken(string token)
        => token is "help" or "-h" or "--help";

    /// <summary>
    /// Returns <c>true</c> if any argument at or after <paramref name="startIndex"/> is a help
    /// token. Subcommand dispatchers whose action verbs take no further positional arguments
    /// (e.g. <c>daemon stop</c>, <c>memory backfill-embeddings</c>, <c>webhooks list</c>) must
    /// not just check the subcommand slot itself for "help"/"-h"/"--help" — a trailing help
    /// token elsewhere in the args was otherwise silently ignored and the verb executed for
    /// real instead of printing help (production canary: <c>netclaw memory backfill-embeddings
    /// --help</c> ran a real provision-and-embed pass; <c>netclaw daemon stop --help</c> would
    /// have actually stopped the daemon). Callers that DO have their own more specific
    /// <c>--help</c> handling for a subcommand (e.g. <c>webhooks set</c>) should exclude that
    /// subcommand from this check so the more specific help text is not shadowed.
    /// </summary>
    public static bool HasTrailingHelpToken(string[] args, int startIndex)
    {
        for (var i = startIndex; i < args.Length; i++)
        {
            if (IsHelpToken(args[i]))
                return true;
        }

        return false;
    }

    public static CliParseResult Parse(string[] args)
    {
        if (args.Length == 0)
            return CliParseResult.NoArgs;

        var first = args[0];

        if (first is "help" or "-h" or "--help")
            return CliParseResult.Help;

        if (first is "version" or "--version" or "-V")
            return CliParseResult.Version;

        if (KnownCommands.Contains(first))
            return new CliParseResult(CliParseKind.Known, first);

        return new CliParseResult(CliParseKind.Unknown, first);
    }
}
