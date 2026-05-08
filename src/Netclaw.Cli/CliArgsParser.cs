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
        "daemon", "mcp", "provider", "model", "reminder",
        "secrets", "config", "update", "pair", "skill", "webhooks",
        "approvals",
    };

    /// <summary>Returns <c>true</c> if the token is a help flag (<c>help</c>, <c>-h</c>, <c>--help</c>).</summary>
    public static bool IsHelpToken(string token)
        => token is "help" or "-h" or "--help";

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
