// -----------------------------------------------------------------------
// <copyright file="DaemonConfigurationSourcesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class DaemonConfigurationSourcesTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Daemon_binds_tools_lists_from_env_then_secrets_then_netclaw_json()
    {
        // Program.cs uses top-level statements, so no test can build the full daemon host.
        // The source check pins the calls. The binding below goes through the same calls.
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Netclaw.Daemon", "Program.cs"));
        Assert.Contains("ConfigReloadBaseline.ReadNetclawJson(bootstrapPaths)", program, StringComparison.Ordinal);
        Assert.Contains("ConfigReloadBaseline.Load(", program, StringComparison.Ordinal);
        Assert.Contains("var policyConfiguration = PolicyConfiguration.Bind(configuration);", program, StringComparison.Ordinal);
        Assert.Contains("var toolConfig = policyConfiguration.Tools;", program, StringComparison.Ordinal);
        Assert.Contains("startupLogger.LogWarning(\"Configuration warning: {ConfigurationWarning}\", warning);", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Get<ToolConfig>", program, StringComparison.Ordinal);

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, """{ "Tools": { "WebFetch": { "HttpAllowList": ["netclaw.json"] } } }""");
        File.WriteAllText(paths.SecretsPath, """{ "Tools": { "WebFetch": { "HttpAllowList": ["secrets.json"] } } }""");
        const string variable = "NETCLAW_Tools__WebFetch__HttpAllowList__0";
        Environment.SetEnvironmentVariable(variable, "environment");
        try
        {
            Assert.Equal(["environment"], BindHttpAllowList(paths));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Assert.Equal(["secrets.json"], BindHttpAllowList(paths));
        File.Delete(paths.SecretsPath);
        Assert.Equal(["netclaw.json"], BindHttpAllowList(paths));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "present", true)]
    [InlineData("loaded", null, false)]
    [InlineData("same", "same", false)]
    [InlineData("loaded", "current", true)]
    public void Startup_reload_requires_a_present_file_with_a_different_hash(
        string? loaded, string? current, bool expected)
        => Assert.Equal(expected, ConfigWatcherActor.NeedsStartupReload(loaded, current));

    public static TheoryData<string, string, string?, string?> CapturedSources => new()
    {
        { "captured_bytes_and_precedence", "/* comment */ { \"Daemon\": { \"Host\": \"loaded\" }, \"ReloadTest\": { \"Base\": \"base\", \"Secret\": \"base\", \"Environment\": \"base\" }, }",
            "{ \"ReloadTest\": { \"Secret\": \"overlay\", \"Environment\": \"overlay\" } }", null },
        { "malformed_base", "{ broken json", null, "netclaw.json" },
        { "malformed_secrets", "{}", "{ broken json", "secrets.json" }
    };

    [Theory]
    [MemberData(nameof(CapturedSources))]
    public void Captured_sources_keep_the_load_hash_precedence_and_failure_identity(
        string name, string captured, string? secrets, string? expectedFile)
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, captured);
        var bytes = ConfigReloadBaseline.ReadNetclawJson(paths);
        File.WriteAllText(paths.NetclawConfigPath, """{ "Daemon": { "Host": "current" } }""");
        if (secrets is not null) File.WriteAllText(paths.SecretsPath, secrets);
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReloadTest:Base"] = "lower", ["ReloadTest:Secret"] = "lower", ["ReloadTest:OnlyLower"] = "lower"
        });
        const string variable = "NETCLAW_ReloadTest__Environment";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "environment");
        try
        {
            if (expectedFile is not null)
            {
                var error = Assert.Throws<InvalidDataException>(() => ConfigReloadBaseline.Load(configuration, paths, bytes));
                Assert.StartsWith("Couldn't read " + expectedFile + ":", DaemonConfigurationSources.DescribeLoadFailure(error));
                return;
            }
            var baseline = ConfigReloadBaseline.Load(configuration, paths, bytes);
            Assert.Equal("loaded", configuration["Daemon:Host"]);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(captured))), baseline.NetclawJsonHash);
            Assert.True(ConfigWatcherActor.NeedsStartupReload(
                baseline.NetclawJsonHash, ConfigReloadBaseline.HashNetclawJson(ConfigReloadBaseline.ReadNetclawJson(paths))), name);
            Assert.Equal("base", configuration["ReloadTest:Base"]);
            Assert.Equal("overlay", configuration["ReloadTest:Secret"]);
            Assert.Equal("environment", configuration["ReloadTest:Environment"]);
            Assert.Equal("lower", configuration["ReloadTest:OnlyLower"]);
            Assert.Equal("lower", baseline.LowerPriorityConfiguration["ReloadTest:Base"]);
            Assert.False(baseline.LowerPriorityConfiguration.ContainsKey("ReloadTest:Environment"));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public void Missing_empty_and_unreadable_files_remain_distinct()
    {
        var paths = new NetclawPaths(_dir.Path);
        Assert.Null(ConfigReloadBaseline.ReadNetclawJson(paths));
        paths.EnsureDirectoriesExist();
        Assert.Null(ConfigReloadBaseline.ReadNetclawJson(paths));
        Assert.Null(ConfigReloadBaseline.HashNetclawJson(null));
        File.WriteAllBytes(paths.NetclawConfigPath, []);
        var bytes = ConfigReloadBaseline.ReadNetclawJson(paths);
        Assert.NotNull(bytes);
        Assert.Empty(bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), ConfigReloadBaseline.HashNetclawJson(bytes));
        File.Delete(paths.NetclawConfigPath);
        Directory.CreateDirectory(paths.NetclawConfigPath);
        var error = Assert.Throws<InvalidDataException>(() => ConfigReloadBaseline.ReadNetclawJson(paths));
        Assert.StartsWith("Couldn't read netclaw.json:", DaemonConfigurationSources.DescribeLoadFailure(error));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Configuration_sources_reject_dangling_links_and_accept_missing_or_readable_files(bool secrets, bool captured)
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        var file = secrets ? paths.SecretsPath : paths.NetclawConfigPath;
        File.WriteAllText(secrets ? paths.NetclawConfigPath : paths.SecretsPath, "{}");
        var target = Path.Combine(_dir.Path, "target.json");

        void Load(string? expected)
        {
            using var stream = captured ? new MemoryStream(Encoding.UTF8.GetBytes("{}")) : null;
            var builder = new ConfigurationBuilder();
            if (captured)
                builder.AddNetclawDaemonSources(paths, stream);
            else
                builder.AddNetclawDaemonSources(paths);
            Assert.Equal(expected, builder.Build()["LinkTest"]);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(file, target);
            Assert.Throws<FileNotFoundException>(() => Load(null));
            File.Delete(file);
            Load(null);
            File.CreateSymbolicLink(file, target);
            File.WriteAllText(target, "{\"LinkTest\":\"loaded\"}");
        }
        else
        {
            Load(null);
            File.WriteAllText(file, "{\"LinkTest\":\"loaded\"}");
        }

        Load("loaded");
    }

    private static List<string> BindHttpAllowList(NetclawPaths paths)
    {
        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(paths).Build();
        return PolicyConfiguration.Bind(configuration).Tools.WebFetch.HttpAllowList;
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
