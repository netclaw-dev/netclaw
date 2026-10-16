// -----------------------------------------------------------------------
// <copyright file="StandInDaemonGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Keeps the tests away from the real daemon of the machine. A command that stops a
/// daemon sends a shutdown notice to the endpoint of its home. A home with no port
/// resolves to port 5199, the port of the real daemon. These scans fail when a test
/// starts a stand-in daemon without a port, or runs the CLI without a daemon endpoint.
/// </summary>
public sealed class StandInDaemonGuardTests
{
    private static readonly string[] HelperFiles = ["StandInDaemon.cs", "SleepStandIn.cs", "StandInDaemonGuardTests.cs"];

    private static readonly Regex StandInExecutable = new(@"\bSleepStandIn\b|File\.Copy\(\s*""/(usr/)?bin/sleep""", RegexOptions.Compiled);

    private static readonly Regex RunsBuiltCli = new(@"""netclaw\.dll""", RegexOptions.Compiled);

    private static readonly Regex GivesCliAnEndpoint = new(
        @"StandInDaemon\.RequireExplicitEndpoint\(|NETCLAW_DAEMON_ENDPOINT",
        RegexOptions.Compiled);

    private static readonly Regex BuildsDaemonManager = new(@"new\s+DaemonManager\s*\(", RegexOptions.Compiled);

    private static readonly Regex WritesPidFile = new(
        @"File\.WriteAll(Text|Lines|Bytes)(Async)?\s*\([^;]*(PidFilePath|netclaw\.pid)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Tests_start_a_stand_in_daemon_only_through_StandInDaemon()
    {
        var root = FindRepoRoot();
        var offenders = TestSources(root)
            .SelectMany(path => FindViolations(path, File.ReadAllText(path)).Select(v => $"{Path.GetRelativePath(root, path)}: {v}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "A test must start a stand-in daemon with StandInDaemon.Start, and must give a CLI child process a daemon "
            + "endpoint. Otherwise a shutdown notice reaches port 5199, the port of the real daemon:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("SleepStandIn.Create(path);", "Foo.cs", true)]
    [InlineData("File.Copy(\"/bin/sleep\", path);", "Foo.cs", true)]
    [InlineData("SleepStandIn.Create(path);", "StandInDaemon.cs", false)]
    [InlineData("var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, \"netclaw.dll\"));", "Foo.cs", true)]
    [InlineData("psi.Add(\"netclaw.dll\"); psi.Environment[\"NETCLAW_DAEMON_ENDPOINT\"] = e;", "Foo.cs", false)]
    [InlineData("psi.Add(\"netclaw.dll\"); StandInDaemon.RequireExplicitEndpoint(home);", "Foo.cs", false)]
    [InlineData("var m = new DaemonManager(p, t); File.WriteAllText(p.PidFilePath, \"1\");", "Foo.cs", true)]
    [InlineData("var m = new DaemonManager(p, t); StandInDaemon.Start(h, d);", "Foo.cs", false)]
    [InlineData("var m = new DaemonManager(p, t);", "Foo.cs", false)]
    [InlineData("File.WriteAllText(p.PidFilePath, \"1\");", "Foo.cs", false)]
    public void The_scan_finds_a_stand_in_daemon_that_has_no_port(string source, string fileName, bool expected)
    {
        Assert.Equal(expected, FindViolations(fileName, source).Count > 0);
    }

    [Fact]
    public void A_test_run_resolves_the_default_home_inside_the_run_temp_root()
    {
        // TestRunTempRoot sets NETCLAW_HOME for the whole test process. Without it, a test that
        // builds NetclawPaths with no argument would resolve ~/.netclaw, the real home.
        var home = new NetclawPaths().BasePath;

        Assert.StartsWith(Netclaw.Tests.TestRunTempRoot.Root, home, StringComparison.Ordinal);
    }

    private static List<string> FindViolations(string path, string source)
    {
        var violations = new List<string>();
        if (HelperFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            return violations;

        if (StandInExecutable.IsMatch(source))
            violations.Add("starts a stand-in daemon outside StandInDaemon");

        if (RunsBuiltCli.IsMatch(source) && !GivesCliAnEndpoint.IsMatch(source))
            violations.Add("runs the CLI with no daemon endpoint (call StandInDaemon.RequireExplicitEndpoint or set NETCLAW_DAEMON_ENDPOINT)");

        if (BuildsDaemonManager.IsMatch(source) && WritesPidFile.IsMatch(source) && !source.Contains("StandInDaemon.", StringComparison.Ordinal))
            violations.Add("writes a PID file for a DaemonManager with no StandInDaemon (the home has no port)");

        return violations;
    }

    private static IEnumerable<string> TestSources(string root)
        => new[] { "src", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
