// -----------------------------------------------------------------------
// <copyright file="TestRunTempRoot.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

// Compiled into each test project that sets <UseTestRunTempRoot>true</UseTestRunTempRoot>
// (see Directory.Build.targets). Not part of any library.
[assembly: AssemblyFixture(typeof(Netclaw.Tests.TestRunTempRootFixture))]

namespace Netclaw.Tests;

/// <summary>
/// Gives one test process a private temp root. The module initializer points
/// TMPDIR, TMP, and TEMP at a new folder, so every <c>Path.GetTempPath()</c>
/// call in the test process and its child processes lands inside it.
/// The assembly fixture fails the run if a test left anything in the root,
/// then deletes the root. Other processes that write to the real temp
/// directory cannot change the result.
/// </summary>
internal static class TestRunTempRoot
{
    private static readonly string[] EnvironmentVariables = ["TMPDIR", "TMP", "TEMP"];

    // The .NET runtime of a child process puts its debugger pipes and diagnostic
    // socket in TMPDIR. A killed child leaves them behind. Verify keeps its own
    // VerifyTempDirectory and VerifyTempFiles. None of these are test output.
    private static readonly string[] RuntimeOwnedPrefixes = ["clr-debug-pipe-", "dotnet-diagnostic-", "VerifyTemp"];

    private static string? _root;

    internal static string Root => _root
        ?? throw new InvalidOperationException("The test run temp root is not initialized.");

    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-testrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _root = root;

        foreach (var name in EnvironmentVariables)
            Environment.SetEnvironmentVariable(name, root);

        // Safety net for a run that ends before the fixture runs. The fixture
        // reports leaks; this handler only deletes.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteRoot();
    }

    internal static string[] FindLeftovers()
        => Directory.Exists(Root)
            ? Directory.EnumerateFileSystemEntries(Root)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !RuntimeOwnedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                .Order()
                .ToArray()
            : [];

    internal static void TryDeleteRoot()
    {
        if (_root is null || !Directory.Exists(_root))
            return;

        try
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (UnauthorizedAccessException) // slopwatch-ignore: SW003 retry after the read-only fix
            {
                // A test can leave a read-only file. Windows refuses to delete it.
                ClearReadOnlyAttributes(_root);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // slopwatch-ignore: SW003 best-effort delete of a folder the run owns
        {
            // A held handle must not hide the leak report from the fixture.
            Console.Error.WriteLine($"The test run could not delete its temp root '{_root}': {ex.Message}");
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
        }
    }
}

/// <summary>
/// Assembly fixture that fails the test run when a test leaves a file or
/// folder in the run temp root.
/// </summary>
public sealed class TestRunTempRootFixture : IDisposable
{
    private static readonly Regex GuidSuffix = new(@"[-_.]?([0-9a-fA-F]{32}|[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}).*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Group by name without the GUID suffix. The prefix names the helper that
    // leaked. The first children and the file extension tell which test wrote it.
    private static string Describe(string name)
    {
        var prefix = GuidSuffix.Replace(name, "*");
        var path = Path.Combine(TestRunTempRoot.Root, name);
        if (!Directory.Exists(path))
            return prefix == name ? $"{name} (file)" : $"{prefix}{Path.GetExtension(name)} (file)";

        var children = Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Order().Take(3);
        return $"{prefix}/ (folder with {string.Join(", ", children)})";
    }

    public void Dispose()
    {
        var leftovers = TestRunTempRoot.FindLeftovers();
        var report = leftovers.Length == 0 ? null : BuildReport(leftovers);
        TestRunTempRoot.TryDeleteRoot();

        if (report is null)
            return;

        // The VSTest adapter shows only "Test Assembly Cleanup Failure" for a fixture
        // exception. Write the names to stderr so a `dotnet test` log still lists them.
        Console.Error.WriteLine(report);
        throw new InvalidOperationException(report);
    }

    private static string BuildReport(string[] leftovers)
    {
        var message = new StringBuilder()
            .AppendLine($"Tests left {leftovers.Length} entries in the test temp root. Each test must delete what it creates.")
            .AppendLine("Use DisposableTempDir, or delete the path in Dispose. Leftovers by name prefix:");
        foreach (var group in leftovers.GroupBy(Describe).OrderByDescending(g => g.Count()))
            message.AppendLine($"  {group.Count(),5}  {group.Key}");

        return message.ToString();
    }
}
