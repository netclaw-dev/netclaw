// -----------------------------------------------------------------------
// <copyright file="StartupConfigurationFailureProcessTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// Runs the built <c>netclawd</c> with an invalid configuration in a temp home. Program.cs's own
/// startup wiring decides the exit code, the stderr text and whether a crash log is written, so
/// only a child process shows all three.
/// </summary>
public sealed class StartupConfigurationFailureProcessTests : IDisposable
{
    private const string Provider = "\"p\":{\"Type\":\"ollama\",\"Endpoint\":\"http://127.0.0.1:9\",\"AuthMethod\":\"None\"}";
    private const string Definitions = "\"Definitions\":{\"d\":{\"Provider\":\"p\",\"ModelId\":\"m\"}}";

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string, string> InvalidConfigs() => new()
    {
        // Models section
        { "{\"Providers\":{" + Provider + "},\"Models\":{" + Definitions + ",\"Roles\":{\"Main\":\"nope\"}}}", "references unknown definition 'nope'" },
        { "{\"Providers\":{" + Provider + "},\"Models\":{\"Definitions\":{\"d\":{\"Provider\":\"p\",\"ModelId\":\"m\",\"ContextWindow\":100}},\"Roles\":{\"Main\":\"d\"}}}", "ContextWindow (100) is below minimum" },
        // Provider sections the Models resolver accepts and startup used to fail on later
        { "{\"Providers\":{\"p\":{\"Type\":\"banana\"}},\"Models\":{" + Definitions + ",\"Roles\":{\"Main\":\"d\"}}}", "Unknown provider type 'banana'. Supported: ollama," },
        { "{\"Providers\":{\"p\":{\"Type\":\"openai-compatible\"}},\"Models\":{" + Definitions + ",\"Roles\":{\"Main\":\"d\"}}}", "Set Providers:p:Endpoint to a URL." },
        { "{\"Providers\":{\"p\":{\"Type\":\"openai\"}},\"Models\":{" + Definitions + ",\"Roles\":{\"Main\":\"d\"}}}", "requires authentication" },
        { "{\"Providers\":{\"p\":{\"Type\":\"ollama\",\"AuthMethod\":\"banana\"}},\"Models\":{" + Definitions + ",\"Roles\":{\"Main\":\"d\"}}}", "Providers:p is invalid" },
        // The configuration source cannot read the file
        { "{\"Models\":{\"Roles\":{\"Main\":\"a\"}},\"models\":{\"Roles\":{\"Main\":\"b\"}}}", "Cannot read netclaw.json or secrets.json" },
        { "{ broken json", "Cannot read netclaw.json or secrets.json" },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public async Task InvalidConfiguration_ExitsOneWithOneLineAndNoCrashLog(string config, string expected)
    {
        var home = Path.Combine(_dir.Path, "home");
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "netclaw.json"), config);

        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir.Path,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "netclawd.dll"));
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("NETCLAW_", StringComparison.Ordinal)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["NETCLAW_HOME"] = home;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // A daemon that did not stop on its own is the failure; do not leave it running.
            process.Kill(entireProcessTree: true);
            Assert.Fail("netclawd kept running with an invalid configuration.");
        }

        Assert.Equal(1, process.ExitCode);
        var errorLines = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Assert.Single(errorLines);
        Assert.StartsWith("error: ", line);
        Assert.Contains(expected, line);
        Assert.DoesNotContain("   at ", await stdout + line);
        Assert.Empty(Directory.GetFiles(Path.Combine(home, "logs"), "crash-*"));
    }
}
