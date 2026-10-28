// -----------------------------------------------------------------------
// <copyright file="DaemonLocalControlConfigTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

[Collection(LegacyModelEnvironmentCollection.Name)]
public sealed class DaemonLocalControlConfigTests : IDisposable
{
    private const string CustomEndpoint = "http://127.0.0.1:6200";
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public DaemonLocalControlConfigTests()
    {
        _paths = new NetclawPaths(Path.Combine(_dir.Path, "command-home"));
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("malformed config same-home stop", "config", false, false)]
    [InlineData("malformed config other-home refusal", "config", true, false)]
    [InlineData("malformed secrets same-home stop", "secrets", false, false)]
    [InlineData("invalid-source readiness", "config", false, true)]
    [InlineData("valid custom endpoint control", "valid", false, false)]
    public async Task Local_operations_require_a_known_address_before_transport(
        string _, string source, bool otherHome, bool readinessOnly)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The process home proof requires Linux /proc.");

        using var daemon = PrepareDaemon(source, otherHome);
        var knownAddress = source == "valid";
        var creations = 0;
        var requests = new List<(HttpMethod Method, Uri Uri)>();
        // Every URI ends here, including an address that a regression derives from defaults.
        var factory = new FakeHttpClientFactory(request =>
        {
            requests.Add((request.Method, request.RequestUri!));
            return knownAddress
                ? FakeHttpMessageHandler.JsonResponse(new
                {
                    pid = daemon.Pid,
                    home = HomeIdentity.Canonical(daemon.Home)
                })
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpClient CreateClient()
        {
            creations++;
            return factory.CreateClient(DaemonApi.LocalControlHttpClientName);
        }

        if (readinessOnly)
        {
            var readiness = await DaemonApi.ProbeLocalReadinessAsync(
                _paths, CreateClient, TestContext.Current.CancellationToken);

            AssertTransport(daemon, creations, requests, knownAddress: false, includeReadiness: false);
            Assert.False(readiness.Ready);
            Assert.Null(readiness.Endpoint);
            Assert.False(daemon.HasExited);
            Assert.Equal(daemon.Pid.ToString(CultureInfo.InvariantCulture), File.ReadAllText(_paths.PidFilePath));
            return;
        }

        var manager = new DaemonManager(
            _paths, TimeProvider.System, new UnsupervisedHost(), new NoSystemCommands(), CreateClient);
        var result = await manager.StopAsync("cli-stop", TestContext.Current.CancellationToken);

        AssertTransport(daemon, creations, requests, knownAddress, includeReadiness: false);
        if (otherHome)
        {
            Assert.False(result.Success, result.Message);
            Assert.True(result.IsRefusal, result.Message);
            Assert.Contains("Nothing was stopped.", result.Message, StringComparison.Ordinal);
            Assert.False(daemon.HasExited);
            Assert.Equal(daemon.Pid.ToString(CultureInfo.InvariantCulture), File.ReadAllText(_paths.PidFilePath));
        }
        else
        {
            Assert.True(result.Success, result.Message);
            Assert.False(result.IsRefusal, result.Message);
            await daemon.Process!.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(daemon.HasExited);
            Assert.False(File.Exists(_paths.PidFilePath));
        }

        if (knownAddress)
        {
            var readiness = await DaemonApi.ProbeLocalReadinessAsync(
                _paths, CreateClient, TestContext.Current.CancellationToken);

            AssertTransport(daemon, creations, requests, knownAddress: true, includeReadiness: true);
            Assert.True(readiness.Ready);
            Assert.Equal(CustomEndpoint, readiness.Endpoint);
        }
    }

    private StandInDaemon PrepareDaemon(string source, bool otherHome)
    {
        var home = otherHome ? Path.Combine(_dir.Path, "other-home") : _paths.BasePath;
        Directory.CreateDirectory(home);
        var daemon = StandInDaemon.Start(home, _dir.Path);
        try
        {
            File.WriteAllText(_paths.PidFilePath, daemon.Pid.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(_paths.NetclawConfigPath,
                source == "config" ? "{" : "{\"Daemon\":{\"Port\":6200}}");
            if (source == "secrets")
                File.WriteAllText(_paths.SecretsPath, "{");

            Assert.False(daemon.HasExited);
            Assert.Equal(!otherHome, HomeIdentity.ProcessServesHome(daemon.Pid, HomeIdentity.Canonical(_paths.BasePath)));
            return daemon;
        }
        catch
        {
            daemon.Dispose();
            throw;
        }
    }

    private void AssertTransport(
        StandInDaemon daemon, int creations, List<(HttpMethod Method, Uri Uri)> requests,
        bool knownAddress, bool includeReadiness)
    {
        var expectedCount = knownAddress ? (includeReadiness ? 2 : 1) : 0;
        Assert.Equal(expectedCount, creations);
        Assert.Equal(expectedCount, requests.Count);
        Assert.Empty(daemon.Requests);
        if (!knownAddress)
            return;

        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal(
            $"{CustomEndpoint}/api/lifecycle/shutdown?reason=cli-stop&pid={daemon.Pid}&home={Uri.EscapeDataString(HomeIdentity.Canonical(_paths.BasePath))}",
            requests[0].Uri.AbsoluteUri);
        if (includeReadiness)
        {
            Assert.Equal(HttpMethod.Get, requests[1].Method);
            Assert.Equal($"{CustomEndpoint}/api/health/ready", requests[1].Uri.AbsoluteUri);
        }
    }

    private sealed class UnsupervisedHost : IContainerSupervisor
    {
        public bool IsExternallySupervised => false;
    }

    private sealed class NoSystemCommands : ISystemCommandRunner
    {
        public Task<SystemCommandResult> RunAsync(string command, string arguments)
            => throw new InvalidOperationException("A local control test must not execute a system command.");
    }
}
