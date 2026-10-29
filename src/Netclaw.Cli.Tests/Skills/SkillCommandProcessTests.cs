// -----------------------------------------------------------------------
// <copyright file="SkillCommandProcessTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Skills;

public sealed class SkillCommandProcessTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Show_and_remove_use_the_daemon_through_the_real_cli_entry_point()
    {
        var paths = new NetclawPaths(_directory.Path);
        paths.EnsureDirectoriesExist();
        var skillDirectory = Directory.CreateDirectory(Path.Combine(paths.SkillsDirectory, "local-proof")).FullName;
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "# Process proof");
        var requests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/skills/local-proof", () =>
        {
            Interlocked.Increment(ref requests);
            return Results.Json(new SkillInventory.Detail
            {
                Skill = new SkillInventory.SkillRow
                {
                    Name = "local-proof", DisplayName = "Local Proof", Description = "Process proof.", Source = "native",
                },
                FilePath = Path.Combine(skillDirectory, "SKILL.md"),
                SkillDirectory = skillDirectory,
                Content = "# Process proof",
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var endpoint = app.Urls.Single();

        var shown = await RunAsync(paths, endpoint, "show");

        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("# Process proof", shown.Output);
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.True(Directory.Exists(skillDirectory));

        var removed = await RunAsync(paths, endpoint, "remove");

        Assert.Equal(0, removed.ExitCode);
        Assert.Equal(2, Volatile.Read(ref requests));
        Assert.False(Directory.Exists(skillDirectory));
    }

    private async Task<(int ExitCode, string Output)> RunAsync(NetclawPaths paths, string endpoint, string verb)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _directory.Path,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "netclaw.dll"));
        foreach (var arg in new[] { "skill", verb, "local-proof" })
            start.ArgumentList.Add(arg);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("NETCLAW_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["HOME"] = _directory.Path;
        start.Environment["NETCLAW_HOME"] = paths.BasePath;
        start.Environment["NETCLAW_DAEMON_ENDPOINT"] = endpoint;
        start.Environment["NETCLAW_Daemon__Port"] = new Uri(endpoint).Port.ToString(CultureInfo.InvariantCulture);
        StandInDaemon.RequireExplicitEndpoint(paths.BasePath, start.Environment);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
