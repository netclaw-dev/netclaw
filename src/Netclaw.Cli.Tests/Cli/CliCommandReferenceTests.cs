// -----------------------------------------------------------------------
// <copyright file="CliCommandReferenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class CliCommandReferenceTests
{
    // Messages that tell the user to run `netclaw run` or `netclaw provider fix` shipped
    // because nothing checked them against the commands the CLI really has. This scans the
    // shipped C# source and the system-skill markdown for a `netclaw <command> [<subcommand>]`
    // reference after a backtick, single quote, parenthesis, an indented string start or a verb such as "Run".
    // The top-level command must be in CliArgsParser.KnownCommands. The subcommand must appear
    // as a quoted literal in that command's own source file (<Command>Command*.cs; the daemon
    // verbs dispatch from Program.cs). Commands with no such file take no subcommand to check.
    private static readonly Regex CommandReference = new(
        @"(?:[`'(]\s*|""\s+|\b(?:[Rr]un|[Uu]se|[Tt]ry|[Ee]xecute|[Tt]ype|[Vv]ia|[Ww]ith|[Uu]sage):? )netclaw (?<command>[A-Za-z][A-Za-z0-9-]*)(?: (?<sub>[a-z][a-z0-9-]*))?",
        RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new("\"(?:\\\\.|[^\"\\\\\\r\\n])*\"", RegexOptions.Compiled);

    [Fact]
    public void User_facing_messages_only_name_commands_the_cli_has()
    {
        var root = FindRepoRoot();
        var srcDir = Path.Combine(root, "src");
        var cliDir = Path.Combine(srcDir, "Netclaw.Cli");
        var cliFiles = SourceFiles(srcDir).Where(f => f.StartsWith(cliDir, StringComparison.Ordinal)).ToList();

        var offenders = new List<string>();
        foreach (var file in SourceFiles(srcDir).Concat(
                     Directory.EnumerateFiles(Path.Combine(root, "feeds"), "*.md", SearchOption.AllDirectories)))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;

                foreach (Match reference in CommandReference.Matches(line))
                {
                    var command = reference.Groups["command"].Value;
                    var sub = reference.Groups["sub"].Value;
                    if (!IsKnown(command, sub, cliFiles))
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{lineNumber} netclaw {command} {sub}".TrimEnd());
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These messages name a netclaw command the CLI does not have:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsKnown(string command, string sub, IReadOnlyList<string> cliFiles)
    {
        if (!CliArgsParser.KnownCommands.Contains(command))
            return false;

        if (sub.Length == 0)
            return true;

        var pascal = char.ToUpperInvariant(command[0]) + command[1..];
        var dispatchers = cliFiles
            .Where(f => Path.GetFileName(f).StartsWith($"{pascal}Command", StringComparison.Ordinal)
                || (command == "daemon" && Path.GetFileName(f) == "Program.cs"))
            .ToList();
        if (dispatchers.Count == 0)
            return true;

        return dispatchers.Any(f => StringLiteral.Matches(File.ReadAllText(f))
            .Any(m => m.Value.Trim('"') == sub));
    }

    private static IEnumerable<string> SourceFiles(string srcDir)
        => Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var segments = Path.GetRelativePath(srcDir, path).Split(Path.DirectorySeparatorChar);
                return !segments.Contains("obj") && !segments.Contains("bin")
                    && !segments[0].EndsWith("Tests", StringComparison.Ordinal)
                    && segments[0] != "Netclaw.SmokeLlmServer";
            });

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
