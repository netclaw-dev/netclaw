// -----------------------------------------------------------------------
// <copyright file="SkillRemoveBoundaryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Xunit;

namespace Netclaw.Cli.Tests.Skills;

public sealed class SkillRemoveBoundaryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("netclaw-skill-remove-").FullName;
    private readonly StringWriter _output = new();
    private int _requests;

    public void Dispose()
    {
        _output.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("future-managed")]
    [InlineData("mcp")]
    public async Task Remove_refuses_an_unknown_or_non_native_source(string source)
    {
        var paths = CreatePaths("home");
        var directory = WriteSkill(paths.SkillsDirectory, "owned");
        var api = CreateApi(paths, Detail(directory, source), DaemonApi.DefaultEndpoint);

        var exit = await SkillCommand.RunAsync(["skill", "remove", "owned"], paths, api, _output);

        Assert.Equal(1, exit);
        Assert.True(File.Exists(Path.Combine(directory, "SKILL.md")));
    }

    [Fact]
    public async Task Remove_refuses_a_remote_daemon_before_any_request_or_local_delete()
    {
        var paths = CreatePaths("home");
        var directory = WriteSkill(paths.SkillsDirectory, "owned");
        var api = CreateApi(paths, Detail(directory, "native"), "https://remote.example");

        var exit = await SkillCommand.RunAsync(["skill", "remove", "owned"], paths, api, _output);

        Assert.Equal(1, exit);
        Assert.Equal(0, _requests);
        Assert.True(File.Exists(Path.Combine(directory, "SKILL.md")));
    }

    [Fact]
    public async Task Remove_refuses_a_nested_link_escape_and_preserves_the_outside_file()
    {
        var paths = CreatePaths("home");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside", "child")).FullName;
        var victim = WriteSkill(outside, "victim");
        Directory.CreateSymbolicLink(Path.Combine(paths.SkillsDirectory, "bridge"), Path.GetDirectoryName(outside)!);
        Directory.CreateSymbolicLink(Path.Combine(paths.SkillsDirectory, "start"),
            Path.Combine(paths.SkillsDirectory, "bridge", "child"));
        var api = CreateApi(paths,
            Detail(Path.Combine(paths.SkillsDirectory, "start", "victim"), "native"),
            DaemonApi.DefaultEndpoint);

        var exit = await SkillCommand.RunAsync(["skill", "remove", "victim"], paths, api, _output);

        Assert.Equal(1, exit);
        Assert.Equal("# Sentinel", File.ReadAllText(Path.Combine(victim, "SKILL.md")));
    }

    [Fact]
    public async Task Remove_accepts_a_native_skill_below_a_symlinked_home()
    {
        var physicalHome = CreatePaths("physical");
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, physicalHome.BasePath);
        var paths = new NetclawPaths(alias);
        var directory = WriteSkill(paths.SkillsDirectory, "owned");
        var api = CreateApi(paths, Detail(directory, "native"), DaemonApi.DefaultEndpoint);

        var exit = await SkillCommand.RunAsync(["skill", "remove", "owned"], paths, api, _output);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(directory));
        Assert.True(Directory.Exists(paths.SkillsDirectory));
    }

    [Fact]
    public async Task Remove_refuses_a_linked_flat_file()
    {
        var paths = CreatePaths("home");
        var outside = WriteSkill(_root, "outside");
        var link = Path.Combine(paths.SkillsDirectory, "flat.md");
        File.CreateSymbolicLink(link, Path.Combine(outside, "SKILL.md"));
        var detail = new SkillInventory.Detail
        {
            Skill = Detail(outside, "native").Skill,
            FilePath = link,
            SkillDirectory = paths.SkillsDirectory,
            IsFlatFile = true,
        };
        var api = CreateApi(paths, detail, DaemonApi.DefaultEndpoint);

        var exit = await SkillCommand.RunAsync(["skill", "remove", "outside"], paths, api, _output);

        Assert.Equal(1, exit);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("# Sentinel", File.ReadAllText(Path.Combine(outside, "SKILL.md")));
    }

    [Fact]
    public async Task Remove_checks_the_directory_even_when_the_file_is_contained()
    {
        var paths = CreatePaths("home");
        var local = WriteSkill(paths.SkillsDirectory, "owned");
        var outside = WriteSkill(_root, "outside");
        var detail = new SkillInventory.Detail
        {
            Skill = Detail(local, "native").Skill,
            FilePath = Path.Combine(local, "SKILL.md"),
            SkillDirectory = outside,
        };
        var api = CreateApi(paths, detail, DaemonApi.DefaultEndpoint);

        var exit = await SkillCommand.RunAsync(["skill", "remove", "owned"], paths, api, _output);

        Assert.Equal(1, exit);
        Assert.Equal("# Sentinel", File.ReadAllText(Path.Combine(outside, "SKILL.md")));
        Assert.True(Directory.Exists(local));
    }

    private NetclawPaths CreatePaths(string name)
    {
        var paths = new NetclawPaths(Path.Combine(_root, name));
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static string WriteSkill(string parent, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), "# Sentinel");
        return directory;
    }

    private static SkillInventory.Detail Detail(string directory, string source)
        => new()
        {
            Skill = new SkillInventory.SkillRow
            {
                Name = Path.GetFileName(directory),
                DisplayName = "Sentinel",
                Description = "A skill whose files must stay within their owner boundary.",
                Source = source,
            },
            FilePath = Path.Combine(directory, "SKILL.md"),
            SkillDirectory = directory,
        };

    private DaemonApi CreateApi(NetclawPaths paths, SkillInventory.Detail detail, string endpoint)
    {
        ClientConfigFile.WriteEndpoint(paths, endpoint);
        var handler = new ResponseHandler(() =>
        {
            _requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(detail) };
        });
        return new DaemonApi(handler, new ConfigurationBuilder().Build(), paths,
            new LocalControlPairingProofProtector(new EphemeralDataProtectionProvider()), TimeProvider.System);
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }
}
