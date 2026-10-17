// -----------------------------------------------------------------------
// <copyright file="StandInDaemonGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.RegularExpressions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Keeps the tests away from the real daemon of the machine. A command that stops a
/// daemon sends a shutdown notice to the endpoint of its home. A home with no port
/// resolves to port 5199, the port of the real daemon. These scans fail when a test
/// starts a stand-in daemon without a port, or runs the CLI or the daemon without one.
/// </summary>
/// <remarks>
/// The scan is a trip wire for the usual mistakes. It reads comment-free source text, so it
/// cannot follow a path that a test builds in a way the patterns do not name. The product
/// check of #2527 (a request with a wrong PID or home gets 409 and stops nothing) is the
/// boundary. The runtime checks <c>StandInDaemon.RequireExplicitEndpoint</c> and
/// <c>StandInDaemon.RequireDaemonBindPort</c> cover the child processes that the scan finds.
/// </remarks>
public sealed class StandInDaemonGuardTests
{
    private static readonly string[] HelperFiles = ["StandInDaemon.cs", "SleepStandIn.cs", "StandInDaemonGuardTests.cs"];

    // SleepStandIn anywhere, or /bin/sleep as an argument of a call that copies or links a file. A test
    // that only runs /bin/sleep (for a timeout, for example) is not a stand-in daemon.
    private static readonly Regex StandInExecutable = new(
        @"\bSleepStandIn\b|(CreateSymbolicLink|CreateHardLink|File\.(Copy|Move))\s*\([^;]*""/(usr/)?bin/sleep""",
        RegexOptions.Compiled);

    // The file name of a stand-in daemon, next to a call that copies or links a file.
    private static readonly Regex StandInName = new(@"""netclawd""", RegexOptions.Compiled);

    private static readonly Regex LinksOrCopiesFile = new(@"CreateSymbolicLink|CreateHardLink|File\.(Copy|Move)\s*\(", RegexOptions.Compiled);

    // Matches "netclaw.dll" and the split form "netclaw" + ".dll".
    private static readonly Regex RunsBuiltCli = new(@"netclaw\.dll|""netclaw""\s*\+\s*""\.dll""", RegexOptions.Compiled);

    private static readonly Regex RunsBuiltDaemon = new(@"netclawd\.dll|""netclawd""\s*\+\s*""\.dll""", RegexOptions.Compiled);

    private static readonly Regex StartsProcess = new(@"ProcessStartInfo|Process\.Start\s*\(", RegexOptions.Compiled);

    // Only the runtime check counts as proof. An endpoint variable alone leaves `daemon stop`,
    // `daemon status`, and `pair` on port 5199, because they ignore it.
    private static readonly Regex GivesCliAnEndpoint = new(@"StandInDaemon\.RequireExplicitEndpoint\(", RegexOptions.Compiled);

    private static readonly Regex GivesDaemonAPort = new(@"StandInDaemon\.RequireDaemonBindPort\(", RegexOptions.Compiled);

    private static readonly Regex BuildsDaemonManager = new(@"new\s+DaemonManager\s*\(", RegexOptions.Compiled);

    // Any mention counts, not only a File.WriteAll call: a test can keep the path in a local first.
    private static readonly Regex NamesPidFile = new(@"PidFilePath|netclaw\.pid", RegexOptions.Compiled);

    [Fact]
    public void Tests_start_a_stand_in_daemon_only_through_StandInDaemon()
    {
        var root = FindRepoRoot();
        var offenders = TestSources(root)
            .SelectMany(path => FindViolations(path, File.ReadAllText(path)).Select(v => $"{Path.GetRelativePath(root, path)}: {v}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "A test must start a stand-in daemon with StandInDaemon.Start, and must give a child process a daemon "
            + "port. Otherwise a shutdown notice reaches port 5199, the port of the real daemon:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("SleepStandIn.Create(path);", "Foo.cs", true)]
    [InlineData("File.Copy(\"/bin/sleep\", path);", "Foo.cs", true)]
    [InlineData("File.Copy(\"/bin/sleep\", Path.Combine(d, \"netclawd\"));", "Foo.cs", true)]
    [InlineData("File.CreateSymbolicLink(Path.Combine(d, \"netclawd\"), \"/usr/bin/sleep\");", "Foo.cs", true)]
    [InlineData("var psi = new ProcessStartInfo(\"/bin/sleep\", \"30\"); using var p = Process.Start(psi);", "Foo.cs", false)]
    [InlineData("File.CreateSymbolicLink(Path.Combine(d, \"netclawd\"), p);", "Foo.cs", true)]
    [InlineData("File.WriteAllText(Path.Combine(d, \"netclawd\"), \"\");", "Foo.cs", false)]
    [InlineData("SleepStandIn.Create(path);", "StandInDaemon.cs", false)]
    [InlineData("var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, \"netclaw.dll\"));", "Foo.cs", true)]
    [InlineData("var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, \"netclaw\" + \".dll\"));", "Foo.cs", true)]
    [InlineData("var psi = new ProcessStartInfo(); psi.Add(\"netclaw.dll\"); psi.Environment[\"NETCLAW_DAEMON_ENDPOINT\"] = e;", "Foo.cs", true)]
    [InlineData("// StandInDaemon.RequireExplicitEndpoint(home, env)\nvar psi = new ProcessStartInfo(); psi.Add(\"netclaw.dll\");", "Foo.cs", true)]
    [InlineData("/* NETCLAW_DAEMON_ENDPOINT */ var psi = new ProcessStartInfo(); psi.Add(\"netclaw.dll\");", "Foo.cs", true)]
    [InlineData("var psi = new ProcessStartInfo(); psi.Add(\"netclaw.dll\"); StandInDaemon.RequireExplicitEndpoint(home, psi.Environment);", "Foo.cs", false)]
    [InlineData("var u = \"http://x\"; // note\nvar psi = new ProcessStartInfo(); psi.Add(\"netclaw.dll\");", "Foo.cs", true)]
    [InlineData("var psi = new ProcessStartInfo(); psi.ArgumentList.Add(\"netclawd.dll\");", "Foo.cs", true)]
    [InlineData("var psi = new ProcessStartInfo(); psi.ArgumentList.Add(\"netclawd.dll\"); StandInDaemon.RequireDaemonBindPort(psi.Environment);", "Foo.cs", false)]
    [InlineData("[InlineData(\"dotnet /opt/netclaw/netclawd.dll\")]", "Foo.cs", false)]
    [InlineData("[InlineData(\"dotnet /opt/netclaw/netclaw.dll\")]", "Foo.cs", false)]
    [InlineData("var m = new DaemonManager(p, t); File.WriteAllText(p.PidFilePath, \"1\");", "Foo.cs", true)]
    [InlineData("var m = new DaemonManager(p, t); var pid = p.PidFilePath; File.WriteAllText(pid, \"1\");", "Foo.cs", true)]
    [InlineData("var m = new DaemonManager(p, t); StandInDaemon.Start(h, d);", "Foo.cs", false)]
    [InlineData("var m = new DaemonManager(p, t);", "Foo.cs", false)]
    [InlineData("File.WriteAllText(p.PidFilePath, \"1\");", "Foo.cs", false)]
    public void The_scan_finds_a_stand_in_daemon_that_has_no_port(string source, string fileName, bool expected)
    {
        Assert.Equal(expected, FindViolations(fileName, source).Count > 0);
    }

    [Theory]
    [InlineData("a // b\nc", "a  \nc")]
    [InlineData("a /* b\nc */ d", "a   d")]
    [InlineData("var u = \"http://x\";", "var u = \"http://x\";")]
    [InlineData("var u = @\"a\"\"//b\";", "var u = @\"a\"\"//b\";")]
    [InlineData("var q = '\"'; // c", "var q = '\"';  ")]
    [InlineData("var u = \"\\\"//b\"; // c", "var u = \"\\\"//b\";  ")]
    public void The_comment_stripper_keeps_strings_and_removes_comments(string source, string expected)
    {
        Assert.Equal(expected, StripComments(source));
    }

    [Fact]
    public void A_test_run_resolves_the_default_home_inside_the_run_temp_root()
    {
        // TestRunTempRoot sets NETCLAW_HOME for the whole test process. Without it, a test that
        // builds NetclawPaths with no argument would resolve ~/.netclaw, the real home.
        var home = new NetclawPaths().BasePath;

        Assert.StartsWith(Netclaw.Tests.TestRunTempRoot.Root, home, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_run_does_not_inherit_a_daemon_endpoint_or_port_from_the_shell()
    {
        // TestRunTempRoot removes these names at process start. An inherited value would send the
        // shutdown notices of the in-process stop paths to the daemon that the shell names.
        var inherited = Environment.GetEnvironmentVariables().Keys
            .OfType<string>()
            .Where(static name => name.Equals("NETCLAW_DAEMON_ENDPOINT", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("NETCLAW_DAEMON__", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(inherited);
    }

    private static List<string> FindViolations(string path, string rawSource)
    {
        var violations = new List<string>();
        if (HelperFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            return violations;

        var source = StripComments(rawSource);

        if (StandInExecutable.IsMatch(source))
            violations.Add("starts a stand-in daemon outside StandInDaemon");

        if (StandInName.IsMatch(source) && LinksOrCopiesFile.IsMatch(source))
            violations.Add("copies or links a file named netclawd outside StandInDaemon");

        if (RunsBuiltCli.IsMatch(source) && StartsProcess.IsMatch(source) && !GivesCliAnEndpoint.IsMatch(source))
            violations.Add("runs the CLI with no daemon port (call StandInDaemon.RequireExplicitEndpoint)");

        if (RunsBuiltDaemon.IsMatch(source) && StartsProcess.IsMatch(source) && !GivesDaemonAPort.IsMatch(source))
            violations.Add("runs netclawd with no daemon port (call StandInDaemon.RequireDaemonBindPort)");

        if (BuildsDaemonManager.IsMatch(source) && NamesPidFile.IsMatch(source) && !source.Contains("StandInDaemon.", StringComparison.Ordinal))
            violations.Add("names a PID file for a DaemonManager with no StandInDaemon (the home has no port)");

        return violations;
    }

    /// <summary>
    /// Replaces each comment with a space. A string, a verbatim string, a raw string, and a char
    /// literal keep their text, so a <c>//</c> inside a URL stays.
    /// </summary>
    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
                result.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                result.Append(' ');
            }
            else if (c == '"' && source.AsSpan(i).StartsWith("\"\"\""))
            {
                var end = source.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                var stop = end < 0 ? source.Length : end + 3;
                result.Append(source, i, stop - i);
                i = stop;
            }
            else if (c == '"')
            {
                var verbatim = i > 0 && (source[i - 1] == '@' || (i > 1 && source[i - 1] == '$' && source[i - 2] == '@'));
                var stop = EndOfString(source, i + 1, verbatim);
                result.Append(source, i, stop - i);
                i = stop;
            }
            else if (c == '\'')
            {
                var stop = i + 1;
                while (stop < source.Length && source[stop] != '\'')
                    stop += source[stop] == '\\' ? 2 : 1;
                stop = Math.Min(stop + 1, source.Length);
                result.Append(source, i, stop - i);
                i = stop;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static int EndOfString(string source, int start, bool verbatim)
    {
        var i = start;
        while (i < source.Length)
        {
            if (!verbatim && source[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (source[i] == '"')
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return source.Length;
    }

    private static IEnumerable<string> TestSources(string root)
        => new[] { "src", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Where(path => IsTestSource(Path.GetRelativePath(root, path)))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // Product code starts the real CLI and daemon by design. Test projects, the shared test
    // utilities, and the tests/ folder are the scope.
    private static bool IsTestSource(string relativePath)
        => relativePath.Split(Path.DirectorySeparatorChar)
            .Any(static segment => segment == "tests" || segment.Contains("Tests", StringComparison.Ordinal));

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
