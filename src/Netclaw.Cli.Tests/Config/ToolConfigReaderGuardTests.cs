// -----------------------------------------------------------------------
// <copyright file="ToolConfigReaderGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

public sealed class ToolConfigReaderGuardTests
{
    // The audience profile defaults depend on the posture, and netclaw.json stores only the
    // keys that the operator changed. A JSON deserializer replaces a partial profile with an
    // empty one, so the CLI then shows and saves grants that the daemon does not use (issue
    // #2362). ConfigFileHelper.LoadToolConfig is the one CLI reader. The pattern matches each
    // generic call with one of these types, because the Security & Access screen read them
    // through a local helper that a list of method names did not cover.
    private static readonly Regex RawToolConfigRead = new(
        @"(?<!AddSingleton|GetRequiredService|GetService)<(ToolConfig|ToolAudienceProfiles|ToolAudienceProfile)>\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void Cli_reads_the_tools_section_only_through_the_daemon_binder()
    {
        var cliDir = Path.Combine(FindRepoRoot(), "src", "Netclaw.Cli");
        var offenders = Directory.EnumerateFiles(cliDir, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => RawToolConfigRead.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(cliDir, path))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Use ConfigFileHelper.LoadToolConfig, not a raw read of the Tools section: " + string.Join(", ", offenders));
    }

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
