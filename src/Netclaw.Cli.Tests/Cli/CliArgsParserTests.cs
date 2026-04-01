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
    [InlineData("secrets")]
    [InlineData("config")]
    [InlineData("update")]
    [InlineData("init")]
    [InlineData("pair")]
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
    public void Parse_unknown_commands_returns_Unknown_with_mode(string command)
    {
        var result = CliArgsParser.Parse([command]);
        Assert.Equal(CliParseKind.Unknown, result.Kind);
        Assert.Equal(command, result.Mode);
    }

    [Fact]
    public void Parse_short_prompt_flag_with_arg_returns_Headless()
    {
        var result = CliArgsParser.Parse(["-p", "hello world"]);
        Assert.Equal(CliParseKind.Headless, result.Kind);
        Assert.Equal("hello world", result.HeadlessPrompt);
    }

    [Fact]
    public void Parse_long_prompt_flag_with_arg_returns_Headless()
    {
        var result = CliArgsParser.Parse(["--prompt", "some query"]);
        Assert.Equal(CliParseKind.Headless, result.Kind);
        Assert.Equal("some query", result.HeadlessPrompt);
    }

    [Fact]
    public void Parse_prompt_flag_without_arg_returns_MissingPromptArg()
    {
        var result = CliArgsParser.Parse(["-p"]);
        Assert.Equal(CliParseKind.MissingPromptArg, result.Kind);
    }

    [Fact]
    public void Parse_long_prompt_flag_without_arg_returns_MissingPromptArg()
    {
        var result = CliArgsParser.Parse(["--prompt"]);
        Assert.Equal(CliParseKind.MissingPromptArg, result.Kind);
    }

    /// <summary>
    /// Guard test: asserts that KnownCommands contains exactly the expected set.
    /// If a new command is added to CliArgsParser.KnownCommands, this test fails,
    /// reminding the author to also add a corresponding mode handler in Program.cs.
    /// Update this set when adding a new command.
    /// </summary>
    [Fact]
    public void KnownCommands_matches_expected_set_of_handled_modes()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "chat", "sessions", "init", "doctor", "status", "stats",
            "daemon", "mcp", "provider", "model", "reminder",
            "secrets", "config", "update", "pair",
        };

        Assert.Equal(expected, CliArgsParser.KnownCommands);
    }
}
