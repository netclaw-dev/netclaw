// -----------------------------------------------------------------------
// <copyright file="BrokenConfigCliTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Runtime.Versioning;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Runs the built CLI as a child process against a netclaw.json or secrets.json that cannot be
/// loaded. Before the fix every command aborted with an unhandled exception (exit 134) and a
/// crash log; now commands that need the config print one line naming the file and exit 1,
/// while doctor, status and help still run.
/// </summary>
public sealed class BrokenConfigCliTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _home;
    private string ConfigPath => Path.Combine(_home, "config", "netclaw.json");
    private string SecretsPath => Path.Combine(_home, "config", "secrets.json");

    public BrokenConfigCliTests()
    {
        _home = Path.Combine(_dir.Path, "netclaw-home");
        Directory.CreateDirectory(Path.Combine(_home, "config"));
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("duplicate key", "{\"configVersion\":1,\"configVersion\":1}", "netclaw.json", "A duplicate key 'configVersion' was found.")]
    [InlineData("empty", "", "netclaw.json", "The input does not contain any JSON tokens.")]
    [InlineData("truncated", "{\"configVersion\":1,\"Daemon\":{", "netclaw.json", "Expected depth to be zero")]
    [InlineData("truncated secrets", "{\"Providers\":{", "secrets.json", "Expected depth to be zero")]
    public async Task Invalid_file_is_reported_by_each_command_instead_of_crashing(string _, string content, string brokenFile, string reason)
    {
        WriteValidConfig();
        File.WriteAllText(brokenFile == "secrets.json" ? SecretsPath : ConfigPath, content);

        // The reason is the parser's own message, not the provider's "Failed to load configuration" wrapper.
        var model = await RunAsync("model", "list");
        Assert.StartsWith($"Couldn't read {brokenFile}: {reason}", model.Output.Trim());

        await AssertReportedAsync(brokenFile);
    }

    [Fact]
    public async Task Dangling_symlink_is_reported_by_each_command_instead_of_crashing()
    {
        // Creating a symbolic link needs a privilege on Windows.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        File.CreateSymbolicLink(ConfigPath, Path.Combine(_dir.Path, "no-such-target.json"));

        await AssertReportedAsync("netclaw.json");
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Unreadable_file_is_reported_by_each_command_instead_of_crashing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "Mode 000 means nothing on Windows, and root reads the file anyway.");

        WriteValidConfig();
        File.SetUnixFileMode(ConfigPath, UnixFileMode.None);
        try
        {
            await AssertReportedAsync("netclaw.json");
        }
        finally
        {
            // The temp directory cannot be cleaned up around an unreadable file.
            File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Config_that_is_a_directory_is_reported_instead_of_crashing()
    {
        Directory.CreateDirectory(ConfigPath);

        var add = await RunAsync("mcp", "add", "x", "--command", "echo");
        var doctor = await RunAsync("doctor");

        Assert.Equal(1, add.ExitCode);
        Assert.Equal("Couldn't read netclaw.json: it is a directory.", add.Output.Trim());
        Assert.Contains("[FAIL] Config Load: Couldn't read netclaw.json: it is a directory.", doctor.Output);
        AssertNoCrashLog();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Unreadable_config_directory_is_reported_instead_of_crashing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "Mode 000 means nothing on Windows, and root reads the directory anyway.");

        WriteValidConfig();
        var configDirectory = Path.GetDirectoryName(ConfigPath)!;
        File.SetUnixFileMode(configDirectory, UnixFileMode.None);
        try
        {
            foreach (var args in new[] { new[] { "doctor" }, ["status"], ["model", "list"], ["config", "--help"] })
            {
                var result = await RunAsync(args);
                Assert.Equal(1, result.ExitCode);
                Assert.StartsWith("Couldn't read ", Assert.Single(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            }
        }
        finally
        {
            File.SetUnixFileMode(configDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        AssertNoCrashLog();
    }

    [Fact]
    public async Task Comments_and_trailing_commas_load_as_they_do_for_the_daemon()
    {
        File.WriteAllText(ConfigPath, "{\n// a comment\n\"configVersion\":1,\n\"McpServers\":{},\n}\n");
        File.WriteAllText(SecretsPath, "{\n// a comment\n\"Foo\":{\"Bar\":\"x\"},\n}\n");
        // Owner-only, so SecretsJsonDoctorCheck goes on to read the values on Linux as it does on Windows.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(SecretsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var mcp = await RunAsync("mcp", "list");
        var provider = await RunAsync("provider", "list");
        var doctor = await RunAsync("doctor");

        Assert.Equal(0, mcp.ExitCode);
        Assert.DoesNotContain("Couldn't read", provider.Output);
        Assert.DoesNotContain("DoctorCheck", doctor.Output);
        Assert.DoesNotContain("Couldn't read", mcp.Output);
        Assert.DoesNotContain("Config Load", doctor.Output);
        Assert.DoesNotContain("[FAIL] Inbound Webhook", doctor.Output);
        Assert.DoesNotContain("[FAIL] exposure-mode", doctor.Output);
        Assert.Contains("[PASS] Config Schema", doctor.Output);
        AssertNoCrashLog();
    }

    [Theory]
    [InlineData("netclaw.json")]
    [InlineData("secrets.json")]
    public async Task Writers_refuse_a_file_with_comments_in_one_line_instead_of_dropping_them(string commented)
    {
        WriteValidConfig();
        var content = "{\n// keep me\n\"configVersion\":1}\n";
        File.WriteAllText(commented == "netclaw.json" ? ConfigPath : SecretsPath, content);

        var fix = await RunAsync("doctor", "--fix", "--yes");
        var add = await RunAsync("mcp", "add", "x", "--command", "echo");

        Assert.Contains($"Nothing was fixed. Couldn't read {commented}: it has comments", fix.Output);
        Assert.Equal(1, add.ExitCode);
        Assert.StartsWith($"Couldn't read {commented}: it has comments", Assert.Single(add.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(content, File.ReadAllText(commented == "netclaw.json" ? ConfigPath : SecretsPath));
        AssertNoCrashLog();
    }

    [Fact]
    public async Task A_writer_names_the_file_it_read_when_both_files_have_comments()
    {
        File.WriteAllText(ConfigPath, "{\n// c\n\"configVersion\":1}\n");
        File.WriteAllText(SecretsPath, "{\n// c\n\"Foo\":\"x\"}\n");

        var secrets = await RunAsync("secrets", "set", "Foo.Bar", "baz");

        Assert.Equal(1, secrets.ExitCode);
        Assert.StartsWith("Couldn't read secrets.json: it has comments", secrets.Output.Trim());
    }

    [Fact]
    public async Task Status_still_finds_a_configured_daemon_port_when_only_secrets_json_cannot_be_read()
    {
        File.WriteAllText(ConfigPath, "{\"configVersion\":1,\"Daemon\":{\"Port\":5987}}");
        File.WriteAllText(SecretsPath, "{\"Providers\":{");

        // This test checks that the CLI reads the port from the home config, so it sets no endpoint.
        // The port in the config is not 5199, so the CLI does not reach the real daemon.
        var status = await RunAsync(["status"], null, daemonEndpoint: null);

        Assert.Contains("127.0.0.1:5987", status.Output);
    }

    [Fact]
    public async Task Commands_that_do_not_need_the_config_work_when_netclaw_json_cannot_be_read()
    {
        File.WriteAllText(ConfigPath, "{\"configVersion\":1,\"configVersion\":1}");

        var secrets = await RunAsync("secrets", "set", "Foo.Bar", "baz");
        var bareSecrets = await RunAsync("secrets");
        var daemonStatus = await RunAsync("daemon", "status");
        var daemonStop = await RunAsync("daemon", "stop");

        Assert.Equal(0, secrets.ExitCode);
        Assert.True(File.Exists(SecretsPath));
        Assert.Contains("Usage:", bareSecrets.Output);
        Assert.DoesNotContain("Couldn't read", daemonStatus.Output);
        Assert.DoesNotContain("Couldn't read", daemonStop.Output);
        AssertNoCrashLog();
    }

    [Fact]
    public async Task Doctor_reports_one_config_failure_and_skips_the_checks_that_need_it()
    {
        File.WriteAllText(ConfigPath, "{\"a\":1,\"a\":2}");

        var doctor = await RunAsync("doctor");
        var fix = await RunAsync("doctor", "--fix", "--yes");

        Assert.Equal(1, doctor.ExitCode);
        Assert.Single(doctor.Output.Split('\n'), line => line.StartsWith("[FAIL]", StringComparison.Ordinal));
        Assert.Contains("[FAIL] Config Load: Couldn't read netclaw.json: A duplicate key 'a' was found.", doctor.Output);
        Assert.Contains("check(s) skipped: the config could not be read.", doctor.Output);
        Assert.DoesNotContain("DoctorCheck", doctor.Output);
        Assert.Contains("Nothing was fixed.", fix.Output);
        AssertNoCrashLog();
    }

    [Fact]
    public async Task Status_exits_non_zero_when_the_config_cannot_be_read_even_if_the_daemon_is_healthy()
    {
        File.WriteAllText(ConfigPath, "{\"configVersion\":1,\"configVersion\":1}");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var body = "{\"overall\":\"healthy\",\"build\":{\"version\":\"0\",\"commitHash\":\"x\",\"buildTimestamp\":\"x\"},"
                + "\"process\":{},\"connectors\":[],\"persistence\":{\"provider\":\"sqlite\"},\"telemetry\":{}}";
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head + body));
        }, TestContext.Current.CancellationToken);

        var status = await RunAsync(["status"], new Dictionary<string, string> { ["NETCLAW_DAEMON_ENDPOINT"] = $"http://127.0.0.1:{port}" });

        Assert.Contains("Couldn't read netclaw.json", status.Output);
        Assert.Equal(1, status.ExitCode);
        listener.Stop();
        await server.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Doctor_fix_leaves_a_loadable_netclaw_json_alone_when_secrets_json_cannot_be_read()
    {
        // Missing configVersion is something --fix would normally write.
        const string config = "{\"Daemon\":{}}";
        File.WriteAllText(ConfigPath, config);
        File.WriteAllText(SecretsPath, "{\"Providers\":{");

        var fix = await RunAsync("doctor", "--fix", "--yes");

        Assert.Contains("Nothing was fixed. Couldn't read secrets.json", fix.Output);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
        AssertNoCrashLog();
    }

    [Theory]
    [InlineData("config")]
    [InlineData("mcp")]
    [InlineData("model")]
    [InlineData("secrets")]
    [InlineData("status")]
    [InlineData("doctor")]
    public async Task Help_works_when_the_config_cannot_be_loaded(string command)
    {
        File.WriteAllText(ConfigPath, "{\"configVersion\":1,");

        var help = await RunAsync(command, "--help");

        Assert.True(help.ExitCode == 0, help.Output);
        Assert.Contains("Usage:", help.Output);
        Assert.DoesNotContain("Couldn't read", help.Output);
        AssertNoCrashLog();
    }

    [Fact]
    public async Task Version_works_when_the_config_cannot_be_loaded()
    {
        File.WriteAllText(ConfigPath, "");

        var version = await RunAsync("--version");

        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith("netclaw ", version.Output);
        AssertNoCrashLog();
    }

    private async Task AssertReportedAsync(string brokenFile)
    {
        var expectedPrefix = $"Couldn't read {brokenFile}:";

        foreach (var args in new[] { new[] { "mcp", "list" }, ["model", "list"], ["mcp", "add", "x", "--command", "echo"], ["config", "set", "Daemon.Port", "5200"] })
        {
            var blocked = await RunAsync(args);
            Assert.Equal(1, blocked.ExitCode);
            var line = Assert.Single(blocked.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.StartsWith(expectedPrefix, line);
        }

        var doctor = await RunAsync("doctor");
        Assert.Equal(1, doctor.ExitCode);
        Assert.Contains($"[FAIL] Config Load: {expectedPrefix}", doctor.Output);
        Assert.Single(doctor.Output.Split('\n'), line => line.StartsWith("[FAIL]", StringComparison.Ordinal));

        // A daemon on the default port can make status exit 0 or 2; with the config unreadable it must not.
        var status = await RunAsync("status");
        Assert.NotEqual(0, status.ExitCode);
        Assert.Contains(expectedPrefix, status.Output);
        Assert.Contains("could not be read from the config", status.Output);
        Assert.DoesNotContain("netclaw daemon start", status.Output);

        AssertNoCrashLog();
    }

    private void WriteValidConfig()
        => File.WriteAllText(ConfigPath, "{\"configVersion\":1}");

    private void AssertNoCrashLog()
    {
        var logs = Path.Combine(_home, "logs");
        var crashLogs = Directory.Exists(logs) ? Directory.GetFiles(logs, "crash-*") : [];
        Assert.Empty(crashLogs);
    }

    // Port 1 has no listener, so the CLI sees no daemon.
    private const string NoDaemonEndpoint = "http://127.0.0.1:1";
    private const string NoDaemonPort = "1";

    private Task<(int ExitCode, string Output)> RunAsync(params string[] args) => RunAsync(args, null);

    private Task<(int ExitCode, string Output)> RunAsync(string[] args, Dictionary<string, string>? environment)
        => RunAsync(args, environment, NoDaemonEndpoint);

    private async Task<(int ExitCode, string Output)> RunAsync(string[] args, Dictionary<string, string>? environment, string? daemonEndpoint)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir.Path,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "netclaw.dll"));
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("NETCLAW_", StringComparison.Ordinal)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["HOME"] = _dir.Path;
        psi.Environment["NETCLAW_HOME"] = _home;
        // The config files of most of these tests are broken, so the port cannot live in them.
        // Without an endpoint, `mcp list` and `doctor` send requests to port 5199, the port of
        // the real daemon.
        // The endpoint variable covers the hub and REST paths. `daemon status`, `daemon stop`, and `pair`
        // ignore it and read the Daemon config, so the environment also gives a port that nothing listens on.
        if (daemonEndpoint is not null)
        {
            psi.Environment["NETCLAW_DAEMON_ENDPOINT"] = daemonEndpoint;
            psi.Environment["NETCLAW_Daemon__Port"] = NoDaemonPort;
        }

        psi.Environment["NETCLAW_Daemon__DisableSelfUpdate"] = "true";
        foreach (var (key, value) in environment ?? [])
            psi.Environment[key] = value;

        StandInDaemon.RequireExplicitEndpoint(_home, psi.Environment);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, (await stdout + await stderr).Replace("\r\n", "\n"));
    }
}
