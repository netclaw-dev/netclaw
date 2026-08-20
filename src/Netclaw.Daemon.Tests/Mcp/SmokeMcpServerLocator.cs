// -----------------------------------------------------------------------
// <copyright file="SmokeMcpServerLocator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Resolves the freshly-built <c>Netclaw.SmokeMcpServer.dll</c> for tests
/// that launch it as a child process. The server is a
/// <c>ReferenceOutputAssembly=false</c> project reference so it is always
/// built alongside the test project; the most recently written copy under
/// the project's <c>bin/</c> tree is the one this test run produced.
/// Picking by write time keeps this correct regardless of build
/// configuration or RID output subdirectory.
/// </summary>
internal static class SmokeMcpServerLocator
{
    /// <summary>
    /// The result cannot change during a test run, so the recursive
    /// <c>bin/</c> walk runs once per test process instead of once per spawn.
    /// The walk is expensive on Windows CI, where every spawning test paid for
    /// it again. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>
    /// also caches the failure: a build that produced no DLL is a permanent
    /// miss, and every test must keep failing with the same assertion.
    /// </summary>
    private static readonly Lazy<string> CachedDll = new(
        LocateDllCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string LocateDll() => CachedDll.Value;

    private static string LocateDllCore()
    {
        var projectDir = Path.Combine(LocateRepositoryRoot(), "tests", "Netclaw.SmokeMcpServer");
        var binMarker = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var dll = Directory
            .EnumerateFiles(projectDir, "Netclaw.SmokeMcpServer.dll", SearchOption.AllDirectories)
            .Where(p => p.Contains(binMarker))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        Assert.True(dll is not null,
            $"Netclaw.SmokeMcpServer.dll not found under {projectDir}/bin — is the project built?");
        return dll!;
    }

    public static string LocateRepositoryRoot()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Netclaw.slnx")))
            repo = repo.Parent;

        Assert.NotNull(repo);
        return repo!.FullName;
    }
}
