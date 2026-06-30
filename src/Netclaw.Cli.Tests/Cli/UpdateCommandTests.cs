// -----------------------------------------------------------------------
// <copyright file="UpdateCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Update;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Configuration.Security;
using Netclaw.Tests.Utilities;
using NSec.Cryptography;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

[Collection("Update verification")]
public sealed class UpdateCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly Key _testSigningKey;
    private readonly byte[] _testPublicKeyBlob;

    public UpdateCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _testSigningKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var pubKeyRaw = _testSigningKey.Export(KeyBlobFormat.RawPublicKey);
        _testPublicKeyBlob = new byte[42];
        _testPublicKeyBlob[0] = 0x45;
        _testPublicKeyBlob[1] = 0x64;
        byte[] testKeyId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Array.Copy(testKeyId, 0, _testPublicKeyBlob, 2, 8);
        Array.Copy(pubKeyRaw, 0, _testPublicKeyBlob, 10, 32);

        MinisignVerifier.TestPublicKeyOverride = _testPublicKeyBlob;
        UpdateCheckService.ResetCache();
    }

    public void Dispose()
    {
        MinisignVerifier.TestPublicKeyOverride = null;
        UpdateCommand.TestHttpMessageHandlerFactory = null;
        UpdateCommand.TestDaemonProcessManagerFactory = null;
        UpdateCommand.TestSystemdUserServiceFactory = null;
        UpdateCheckService.ResetCache();
        _testSigningKey.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task StopDaemonForUpdateAsync_UsesSystemd_WhenServiceOwnsLifecycle()
    {
        var manager = new FakeDaemonUpdateProcessManager();
        manager.EnqueueStatus(NotRunning());
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        var systemd = CreateSystemdService(runner);

        var result = await UpdateCommand.StopDaemonForUpdateAsync(manager, systemd, Running());

        Assert.True(result.Success);
        Assert.Equal(UpdateDaemonOwner.SystemdUserService, result.Owner);
        Assert.Equal(
            [
                ("systemctl", "--user is-active --quiet netclaw.service"),
                ("systemctl", "--user stop netclaw.service")
            ],
            runner.Commands);
        Assert.Equal(0, manager.StopCalls);
        Assert.Equal(0, manager.StartCalls);
    }

    [Fact]
    public async Task StopDaemonForUpdateAsync_StopsDetachedDaemon_WhenSystemdStopLeavesDaemonRunning()
    {
        var manager = new FakeDaemonUpdateProcessManager();
        manager.EnqueueStatus(Running());
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        var systemd = CreateSystemdService(runner);

        var result = await UpdateCommand.StopDaemonForUpdateAsync(manager, systemd, Running());

        Assert.True(result.Success);
        Assert.Equal(UpdateDaemonOwner.SystemdUserService, result.Owner);
        Assert.Equal(
            [
                ("systemctl", "--user is-active --quiet netclaw.service"),
                ("systemctl", "--user stop netclaw.service")
            ],
            runner.Commands);
        Assert.Equal(1, manager.StopCalls);
        Assert.Equal("update", manager.StopReasons.Single());
    }

    [Fact]
    public async Task StopDaemonForUpdateAsync_Fails_WhenSystemdOwnershipIsUnknown()
    {
        var manager = new FakeDaemonUpdateProcessManager();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(1, "Failed to connect to bus"));
        runner.Enqueue(new SystemCommandResult(1, string.Empty));
        var systemd = CreateSystemdService(runner);

        var result = await UpdateCommand.StopDaemonForUpdateAsync(manager, systemd, Running());

        Assert.False(result.Success);
        Assert.Equal(UpdateDaemonOwner.None, result.Owner);
        Assert.Contains("Could not determine", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(("systemctl", "--user stop netclaw.service"), runner.Commands);
        Assert.Equal(0, manager.StopCalls);
    }

    [Fact]
    public async Task StopDaemonForUpdateAsync_UsesDetachedLifecycle_WhenSystemdDoesNotOwnDaemon()
    {
        var manager = new FakeDaemonUpdateProcessManager();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(3, string.Empty));
        runner.Enqueue(new SystemCommandResult(1, string.Empty));
        var systemd = CreateSystemdService(runner);

        var result = await UpdateCommand.StopDaemonForUpdateAsync(manager, systemd, Running());

        Assert.True(result.Success);
        Assert.Equal(UpdateDaemonOwner.DetachedProcess, result.Owner);
        Assert.DoesNotContain(("systemctl", "--user stop netclaw.service"), runner.Commands);
        Assert.Equal(1, manager.StopCalls);
        Assert.Equal("update", manager.StopReasons.Single());
    }

    [Fact]
    public async Task StartDaemonAfterUpdateAsync_UsesSystemd_ForSystemdOwnedDaemon()
    {
        var manager = new FakeDaemonUpdateProcessManager();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(0, string.Empty));
        var systemd = CreateSystemdService(runner);

        var result = await UpdateCommand.StartDaemonAfterUpdateAsync(
            UpdateDaemonOwner.SystemdUserService,
            manager,
            systemd);

        Assert.True(result.Success);
        Assert.Equal([("systemctl", "--user start netclaw.service")], runner.Commands);
        Assert.Equal(0, manager.StartCalls);
    }

    [Fact]
    public async Task StartDaemonAfterUpdateAsync_PropagatesSystemdStartFailure()
    {
        var manager = new FakeDaemonUpdateProcessManager();
        var runner = new FakeSystemCommandRunner();
        runner.Enqueue(new SystemCommandResult(1, "unit failed"));
        var systemd = CreateSystemdService(runner);

        var result = await UpdateCommand.StartDaemonAfterUpdateAsync(
            UpdateDaemonOwner.SystemdUserService,
            manager,
            systemd);

        Assert.False(result.Success);
        Assert.Contains("unit failed", result.Message, StringComparison.Ordinal);
        Assert.Equal([("systemctl", "--user start netclaw.service")], runner.Commands);
        Assert.Equal(0, manager.StartCalls);
    }

    [Fact]
    public async Task StartDaemonAfterUpdateAsync_UsesDetachedLifecycle_ForDetachedDaemon()
    {
        var manager = new FakeDaemonUpdateProcessManager();
        var runner = new FakeSystemCommandRunner();
        var systemd = CreateSystemdService(runner);

        var result = await UpdateCommand.StartDaemonAfterUpdateAsync(
            UpdateDaemonOwner.DetachedProcess,
            manager,
            systemd);

        Assert.True(result.Success);
        Assert.Equal(1, manager.StartCalls);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task RunAsync_BlocksInstall_WhenDisableSelfUpdateIsConfigured()
    {
        var manifest = CreateManifest("99.0.0", UpdateCheckService.GetCurrentRid());
        UpdateCommand.TestHttpMessageHandlerFactory = () => CreateSignedHandler(manifest);

        using var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);

        try
        {
            var exitCode = await UpdateCommand.RunAsync(["update"], _paths, selfUpdateDisabled: true);

            Assert.Equal(1, exitCode);
            Assert.Contains("Self-update is disabled", stdout.ToString());
            Assert.Contains("Pull a newer container image to upgrade.", stdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Theory]
    [InlineData("beta", "beta")]
    [InlineData("stable", "stable")]
    public async Task RunAsync_PersistsChannel_WhenSwitched(string arg, string expectedWire)
    {
        var manifest = CreateManifest("99.0.0", UpdateCheckService.GetCurrentRid());
        UpdateCommand.TestHttpMessageHandlerFactory = () => CreateSignedHandler(manifest);

        using var stdout = new StringWriter();
        var originalOut = Console.Out;
        var originalIn = Console.In;
        Console.SetOut(stdout);
        // An update is available; decline the install prompt so this exercises
        // only channel switching + persistence, not the download path.
        Console.SetIn(new StringReader("n\n"));

        try
        {
            var exitCode = await UpdateCommand.RunAsync(
                ["update", "--channel", arg], _paths);

            Assert.Equal(0, exitCode);
            Assert.Equal(expectedWire, ReadPersistedChannel());
            Assert.Contains($"Update channel set to '{expectedWire}'", stdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotPersistChannel_UnderCheck()
    {
        var manifest = CreateManifest("99.0.0", UpdateCheckService.GetCurrentRid());
        UpdateCommand.TestHttpMessageHandlerFactory = () => CreateSignedHandler(manifest);

        using var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);

        try
        {
            var exitCode = await UpdateCommand.RunAsync(
                ["update", "--check", "--channel", "beta"], _paths);

            Assert.Equal(0, exitCode);
            // --check is read-only: the channel is previewed for this run, not written to disk.
            Assert.False(File.Exists(_paths.NetclawConfigPath));
            Assert.Contains("Checking 'beta' channel", stdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsUnknownChannel()
    {
        using var stderr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(stderr);

        try
        {
            var exitCode = await UpdateCommand.RunAsync(["update", "--channel", "nightly"], _paths);

            Assert.Equal(1, exitCode);
            Assert.Contains("Unknown channel", stderr.ToString());
            Assert.False(File.Exists(_paths.NetclawConfigPath));
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    private string? ReadPersistedChannel()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        return doc.RootElement.GetProperty("Daemon").GetProperty("UpdateChannel").GetString();
    }

    [Theory]
    [MemberData(nameof(StartupUpdateSkippedCases))]
    public void ShouldRunStartupUpdateCheck_ReturnsFalse_ForInteractiveOrSelfUpdateFlows(string[] args)
    {
        Assert.False(UpdateCommand.ShouldRunStartupUpdateCheck(args[0], args));
    }

    [Theory]
    [MemberData(nameof(StartupUpdateAllowedCases))]
    public void ShouldRunStartupUpdateCheck_ReturnsTrue_ForNonInteractiveFlows(string[] args)
    {
        Assert.True(UpdateCommand.ShouldRunStartupUpdateCheck(args[0], args));
    }

    public static IEnumerable<object[]> StartupUpdateSkippedCases()
    {
        yield return [new[] { "init" }];
        yield return [new[] { "update" }];
        yield return [new[] { "secrets", "set", "Discord:BotToken", "token" }];
        yield return [new[] { "daemon", "stop" }];
        yield return [new[] { "chat" }];
        yield return [new[] { "chat", "-p", "hello" }];
        yield return [new[] { "sessions" }];
        yield return [new[] { "sessions", "--once" }];
        yield return [new[] { "stats", "--tui" }];
        yield return [new[] { "mcp", "tools" }];
        yield return [new[] { "mcp", "permissions" }];
        yield return [new[] { "provider" }];
        yield return [new[] { "model" }];
        yield return [new[] { "approvals" }];
        yield return [new[] { "approvals", "tui" }];
        yield return [new[] { "reminder", "ui" }];
        yield return [new[] { "reminder", "tui" }];
    }

    public static IEnumerable<object[]> StartupUpdateAllowedCases()
    {
        yield return [new[] { "status" }];
        yield return [new[] { "doctor" }];
        yield return [new[] { "stats", "--json" }];
        yield return [new[] { "mcp", "list" }];
        yield return [new[] { "mcp", "tools", "allow", "shell" }];
        yield return [new[] { "provider", "list" }];
        yield return [new[] { "model", "list" }];
        yield return [new[] { "approvals", "list" }];
        yield return [new[] { "reminder", "validate" }];
    }

    private FakeHttpMessageHandler CreateSignedHandler(BinaryFeedManifest manifest)
    {
        var handler = new FakeHttpMessageHandler();
        var json = JsonSerializer.Serialize(manifest);
        handler.AddResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.OK, json, "application/json");

        var sigContent = SignContent(json);
        handler.AddResponse(FeedConstants.BinaryManifestSignatureUrl, HttpStatusCode.OK, sigContent, "text/plain");

        return handler;
    }

    private string SignContent(string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        var signature = SignatureAlgorithm.Ed25519.Sign(_testSigningKey, data);

        var sigBlob = new byte[74];
        sigBlob[0] = 0x45;
        sigBlob[1] = 0x44;
        Array.Copy(_testPublicKeyBlob, 2, sigBlob, 2, 8);
        Array.Copy(signature, 0, sigBlob, 10, 64);

        return $"untrusted comment: test signature\n{Convert.ToBase64String(sigBlob)}\ntrusted comment: test\ndGVzdA==\n";
    }

    private static BinaryFeedManifest CreateManifest(string version, string rid)
    {
        return new BinaryFeedManifest
        {
            Latest = version,
            UpdatedAt = DateTimeOffset.UtcNow,
            Releases =
            [
                new BinaryRelease
                {
                    Version = version,
                    ReleasedAt = DateTimeOffset.UtcNow,
                    Assets =
                    [
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = rid,
                            Url = $"https://releases.netclaw.dev/{version}/netclaw-{version}-{rid}.tar.gz",
                            Sha256 = "abc123",
                            SizeBytes = 50_000_000
                        }
                    ]
                }
            ]
        };
    }

    private static DaemonStatus Running() => new(true, 123, "Daemon running.");

    private static DaemonStatus NotRunning() => new(false, null, "Daemon is not running.");

    private SystemdUserService CreateSystemdService(FakeSystemCommandRunner runner)
    {
        var unitPath = Path.Combine(_dir.Path, "netclaw.service");
        File.WriteAllText(unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
        return new SystemdUserService(unitPath, runner, enabledOnThisPlatform: true);
    }

    private sealed class FakeDaemonUpdateProcessManager : IDaemonProcessLifecycle
    {
        private readonly Queue<DaemonStatus> _statuses = [];
        private DaemonStatus _lastStatus = NotRunning();

        public int StopCalls { get; private set; }

        public int StartCalls { get; private set; }

        public List<string> StopReasons { get; } = [];

        public DaemonResult StopResult { get; set; } = new(true, "detached stopped");

        public DaemonResult StartResult { get; set; } = new(true, "detached started");

        public void EnqueueStatus(DaemonStatus status) => _statuses.Enqueue(status);

        public DaemonStatus GetStatus()
        {
            if (_statuses.TryDequeue(out var status))
                _lastStatus = status;

            return _lastStatus;
        }

        public Task<DaemonResult> StopAsync(string reason, CancellationToken cancellationToken)
        {
            StopCalls++;
            StopReasons.Add(reason);
            return Task.FromResult(StopResult);
        }

        public DaemonResult Start()
        {
            StartCalls++;
            return StartResult;
        }
    }

    private sealed class FakeSystemCommandRunner : ISystemCommandRunner
    {
        private readonly Queue<SystemCommandResult> _results = [];

        public List<(string Command, string Arguments)> Commands { get; } = [];

        public void Enqueue(SystemCommandResult result) => _results.Enqueue(result);

        public Task<SystemCommandResult> RunAsync(string command, string arguments)
        {
            Commands.Add((command, arguments));
            return Task.FromResult(_results.Count == 0
                ? new SystemCommandResult(1, string.Empty)
                : _results.Dequeue());
        }
    }

}
