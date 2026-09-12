// -----------------------------------------------------------------------
// <copyright file="CliArgsParserTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Cli;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class CliArgsParserTests
{
    [Fact]
    public void Parse_no_args_returns_NoArgs()
    {
        var result = CliArgsParser.Parse([]);
        Assert.Equal(CliParseKind.NoArgs, result.Kind);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_help_tokens_returns_Help(string arg)
    {
        var result = CliArgsParser.Parse([arg]);
        Assert.Equal(CliParseKind.Help, result.Kind);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-V")]
    public void Parse_version_tokens_returns_Version(string arg)
    {
        var result = CliArgsParser.Parse([arg]);
        Assert.Equal(CliParseKind.Version, result.Kind);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("sessions")]
    [InlineData("doctor")]
    [InlineData("status")]
    [InlineData("stats")]
    [InlineData("daemon")]
    [InlineData("mcp")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("reminder")]
    [InlineData("memory")]
    [InlineData("secrets")]
    [InlineData("config")]
    [InlineData("update")]
    [InlineData("init")]
    [InlineData("pair")]
    [InlineData("skill")]
    [InlineData("webhooks")]
    public void Parse_known_commands_returns_Known_with_mode(string command)
    {
        var result = CliArgsParser.Parse([command]);
        Assert.Equal(CliParseKind.Known, result.Kind);
        Assert.Equal(command, result.Mode);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("bar")]
    [InlineData("unknown-command")]
    [InlineData("frobble")]
    [InlineData("-p")]
    [InlineData("--prompt")]
    public void Parse_unknown_commands_returns_Unknown_with_mode(string command)
    {
        var result = CliArgsParser.Parse([command]);
        Assert.Equal(CliParseKind.Unknown, result.Kind);
        Assert.Equal(command, result.Mode);
    }

    /// <summary>
    /// Regression test for the alpha.onnx.2 production canary: <c>netclaw memory</c> had a
    /// working mode handler in Program.cs (<c>if (mode is "memory")</c>) and was advertised in
    /// <c>--help</c>, but "memory" was missing from <see cref="CliArgsParser.KnownCommands"/>, so
    /// the parser classified it as <see cref="CliParseKind.Unknown"/> before dispatch ever reached
    /// the handler. Exercise the exact failing invocation shape (subcommand + a help-style flag)
    /// to prove it now resolves to the known "memory" command.
    /// </summary>
    [Theory]
    [InlineData("backfill-embeddings")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_memory_command_resolves_to_Known_not_Unknown(string secondArg)
    {
        var result = CliArgsParser.Parse(["memory", secondArg]);
        Assert.Equal(CliParseKind.Known, result.Kind);
        Assert.Equal("memory", result.Mode);
    }

    /// <summary>
    /// Guard test: derives the "known command" ground truth directly from Program.cs source
    /// instead of a hand-maintained mirror list. The previous version of this test hardcoded
    /// its own copy of the expected set, so when "memory" gained a mode handler (Program.cs
    /// <c>if (mode is "memory")</c>) and a `--help` listing but was never added to
    /// <see cref="CliArgsParser.KnownCommands"/>, nothing caught the drift — the "expected" set
    /// was just a second hand-typed copy of the same (incomplete) list, not an independent check.
    ///
    /// This version checks both directions against real source content:
    ///  - every command dispatched via `if (mode is "...")` in Program.cs must be in
    ///    KnownCommands (a mode handler with no parser entry is unreachable — this is exactly
    ///    the canary bug), and
    ///  - every command listed in the `--help` "Commands:" section must be in KnownCommands
    ///    (an advertised command the parser rejects is a user-facing regression), and
    ///  - KnownCommands must not contain anything beyond the union of the two (an entry with
    ///    no backing handler or help listing is unreachable/dead documentation-wise).
    /// </summary>
    [Fact]
    public void KnownCommands_matches_every_mode_handler_and_help_listed_command()
    {
        var programSource = ReadProgramCsSource();

        var dispatchedModes = ExtractDispatchedModeTokens(programSource);
        var helpListedCommands = ExtractHelpListedCommands(programSource);

        Assert.Contains("memory", dispatchedModes);
        Assert.Contains("memory", helpListedCommands);

        var expected = new HashSet<string>(dispatchedModes, StringComparer.Ordinal);
        expected.UnionWith(helpListedCommands);

        Assert.Equal(expected, CliArgsParser.KnownCommands);
    }

    /// <summary>
    /// Extracts every literal mode token dispatched via <c>if (mode is "x")</c> or
    /// <c>if (mode is "x" or "y")</c> in Program.cs — the actual mode-handler ground truth the
    /// KnownCommands doc comment refers to ("must stay in sync with the mode handlers").
    /// </summary>
    private static IReadOnlySet<string> ExtractDispatchedModeTokens(string programSource)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match clauseMatch in Regex.Matches(programSource, @"if \(mode is (?<clause>.*?)\)"))
        {
            foreach (Match tokenMatch in Regex.Matches(clauseMatch.Groups["clause"].Value, "\"([a-zA-Z-]+)\""))
            {
                tokens.Add(tokenMatch.Groups[1].Value);
            }
        }

        Assert.NotEmpty(tokens);
        return tokens;
    }

    /// <summary>
    /// Extracts every command name listed in <c>WriteGeneralHelp()</c>'s "Commands:" section
    /// (the first whitespace/comma-delimited token of each line), skipping "version" since it
    /// resolves via the distinct <see cref="CliParseKind.Version"/> path rather than
    /// <see cref="CliArgsParser.KnownCommands"/>.
    /// </summary>
    private static IReadOnlySet<string> ExtractHelpListedCommands(string programSource)
    {
        var sectionMatch = Regex.Match(
            programSource,
            "Console\\.WriteLine\\(\"Commands:\"\\);(?<body>.*?)Console\\.WriteLine\\(\"Run `netclaw",
            RegexOptions.Singleline);
        Assert.True(sectionMatch.Success, "Could not locate the 'Commands:' help section in Program.cs.");

        var commands = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match lineMatch in Regex.Matches(sectionMatch.Groups["body"].Value, "Console\\.WriteLine\\(\"  (?<line>[^\"]*)\"\\);"))
        {
            var firstToken = lineMatch.Groups["line"].Value
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (firstToken is null or "version")
                continue;

            commands.Add(firstToken);
        }

        Assert.NotEmpty(commands);
        return commands;
    }

    /// <summary>
    /// Regression coverage for the canary "help executes instead of printing help" family of
    /// bugs (<c>netclaw memory backfill-embeddings --help</c> ran a real embed pass;
    /// <c>netclaw daemon stop --help</c> would have actually stopped the daemon). Every fix
    /// site (MemoryCommand, the Program.cs daemon dispatch, WebhooksCommand, ReminderCommand)
    /// routes through this one helper, so its own scan logic only needs proving once.
    /// </summary>
    [Theory]
    [InlineData(new[] { "memory", "backfill-embeddings" }, false)]
    [InlineData(new[] { "memory", "backfill-embeddings", "--force" }, false)]
    [InlineData(new[] { "memory", "backfill-embeddings", "--help" }, true)]
    [InlineData(new[] { "memory", "backfill-embeddings", "-h" }, true)]
    [InlineData(new[] { "memory", "backfill-embeddings", "help" }, true)]
    [InlineData(new[] { "daemon", "stop" }, false)]
    [InlineData(new[] { "daemon", "stop", "--help" }, true)]
    public void HasTrailingHelpToken_scans_from_startIndex(string[] args, bool expected)
    {
        Assert.Equal(expected, CliArgsParser.HasTrailingHelpToken(args, startIndex: 2));
    }

    [Fact]
    public void HasTrailingHelpToken_ignores_tokens_before_startIndex()
    {
        // The subcommand itself ("help") sits at index 1, before startIndex — this helper is
        // only meant to scan trailing args, so it must not double-count the subcommand slot.
        Assert.False(CliArgsParser.HasTrailingHelpToken(["memory", "help"], startIndex: 2));
    }

    [Fact]
    public void HasTrailingHelpToken_returns_false_for_empty_tail()
    {
        Assert.False(CliArgsParser.HasTrailingHelpToken(["memory", "backfill-embeddings"], startIndex: 2));
    }

    private static string ReadProgramCsSource() => File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Netclaw.Cli", "Program.cs"));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION_PLAN.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
