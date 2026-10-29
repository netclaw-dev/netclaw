// -----------------------------------------------------------------------
// <copyright file="SkillCommandProcessTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

        var shown = await RunAsync(paths, endpoint, ["show", "local-proof"]);

        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("# Process proof", shown.Output);
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.True(Directory.Exists(skillDirectory));

        var removed = await RunAsync(paths, endpoint, ["remove", "local-proof"]);

        Assert.Equal(0, removed.ExitCode);
        Assert.Equal(2, Volatile.Read(ref requests));
        Assert.False(Directory.Exists(skillDirectory));
    }

    [Fact]
    public async Task Offline_search_uses_both_config_sections_and_the_shared_source_labels()
    {
        var paths = new NetclawPaths(_directory.Path);
        paths.EnsureDirectoriesExist();
        var externalRoot = Path.Combine(_directory.Path, "external");
        WriteSkill(paths.SkillsDirectory, "local-proof");
        WriteSkill(paths.SystemSkillsDirectory, "system-proof");
        WriteSkill(paths.ServerFeedDirectory("team"), "feed-proof");
        WriteSkill(externalRoot, "external-proof");
        File.WriteAllText(paths.NetclawConfigPath, JsonSerializer.Serialize(new
        {
            SkillFeeds = new SkillFeedsConfig
            {
                Feeds = [new SkillFeedSource { Name = "team", Url = "https://skills.example" }],
            },
            ExternalSkills = new ExternalSkillsConfig
            {
                Sources = [new ExternalSkillSource { Name = "external", Path = externalRoot }],
            },
        }));

        var result = await RunAsync(paths, "http://127.0.0.1:1", ["search", "proof"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"(?m)^local-proof\s+native\s+", result.Output);
        Assert.Matches(@"(?m)^system-proof\s+system\s+", result.Output);
        Assert.Matches(@"(?m)^feed-proof\s+server-feed\s+", result.Output);
        Assert.Matches(@"(?m)^external-proof\s+external\s+", result.Output);
    }

    private static void WriteSkill(string parent, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        File.WriteAllText(Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: A source discovery proof.\nversion: 1.0.0\n---\n# Proof\n");
    }

    private async Task<(int ExitCode, string Output)> RunAsync(NetclawPaths paths, string endpoint, string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _directory.Path,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "netclaw.dll"));
        foreach (var arg in new[] { "skill" }.Concat(arguments))
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
