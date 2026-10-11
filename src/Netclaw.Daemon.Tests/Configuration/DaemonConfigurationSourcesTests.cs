// -----------------------------------------------------------------------
// <copyright file="DaemonConfigurationSourcesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
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
        Assert.Contains("configuration.AddNetclawDaemonSources(bootstrapPaths);", program, StringComparison.Ordinal);
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
