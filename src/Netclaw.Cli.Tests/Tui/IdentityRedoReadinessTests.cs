// -----------------------------------------------------------------------
// <copyright file="IdentityRedoReadinessTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina.Reactive;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class IdentityRedoReadinessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_final_save_screen_releases_Enter_before_the_UI_dispatch_ack(bool writeFails)
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        if (writeFails) Directory.CreateDirectory(paths.ToolingPath);
        using var daemon = new DaemonReadinessFixture(paths);
        using var vm = new IdentityRedoViewModel(paths, new ChatNavigationState(), daemon.Step);
        var dispatches = Channel.CreateUnbounded<(Action Action, TaskCompletionSource Ack)>();
        Func<Action, CancellationToken, Task> dispatch = (action, _) =>
        {
            var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatches.Writer.TryWrite((action, ack));
            return ack.Task;
        };
        typeof(ReactiveViewModel).GetField("_invokeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, dispatch);
        for (var index = 0; index < 4; index++) vm.GoNext();
        var operation = vm.OperationCompletion;
        Assert.False(operation.IsCompleted);
        var final = await dispatches.Reader.ReadAsync(TestContext.Current.CancellationToken);
        if (writeFails)
        {
            var error = Record.Exception(final.Action);
            Assert.NotNull(error);
            final.Ack.SetException(error);
            final = await dispatches.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        try
        {
            final.Action();
            Assert.Equal(!writeFails, vm.IsSaved.Value);
            if (writeFails) Assert.Contains("Couldn't write TOOLING.md", vm.Context.StatusMessage.Value);
            Assert.True(vm.OperationCompletion.IsCompleted);
            Assert.False(operation.IsCompleted);
        }
        finally { final.Ack.TrySetResult(); }
        await operation;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guided_chat_waits_for_the_saved_identity_generation_and_repeated_Enter_shares_the_wait(bool quit)
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, """{"configVersion":1,"Identity":{"UserName":"Before"}}""");
        using var daemon = new DaemonReadinessFixture(paths) { AdvanceGeneration = false };
        var navigation = new ChatNavigationState();
        using var vm = new IdentityRedoViewModel(paths, navigation, daemon.Step);
        var routes = new List<string>();
        IdentityRedoViewModelTests.SetNavigate(vm, routes.Add);
        vm.Step.UserName = "After";
        for (var index = 0; index < 4; index++) vm.GoNext();
        await vm.OperationCompletion;
        Assert.True(vm.IsSaved.Value);
        Assert.Equal(1, daemon.Probes);
        Assert.Contains("Before", daemon.ConfigAtFirstProbe);
        Assert.Contains("After", File.ReadAllText(paths.NetclawConfigPath));
        vm.GoNext();
        var wait = vm.OperationCompletion;
        vm.GoNext();
        Assert.Same(wait, vm.OperationCompletion);
        Assert.False(wait.IsCompleted);
        Assert.Empty(routes);
        Assert.False(navigation.IsOnboarding);
        if (quit) vm.GoBack();
        else { daemon.Generation = 2; daemon.Clock.Advance(TimeSpan.FromSeconds(1)); }
        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(quit ? 0 : 1, routes.Count);
        Assert.Equal(!quit, navigation.IsOnboarding);
        if (!quit) Assert.Equal(ChatViewModel.Route, Assert.Single(routes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_generation_or_probe_failure_blocks_the_identity_write(bool probeFails)
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        const string config = """{"configVersion":1,"Identity":{"UserName":"Before"}}""";
        File.WriteAllText(paths.NetclawConfigPath, config);
        using var daemon = new DaemonReadinessFixture(paths) { ReportGeneration = false };
        if (probeFails) daemon.Failure = new HttpRequestException("probe unavailable");
        var navigation = new ChatNavigationState();
        using var vm = new IdentityRedoViewModel(paths, navigation, daemon.Step);
        for (var index = 0; index < 4; index++) vm.GoNext();
        await vm.OperationCompletion;
        Assert.False(vm.IsSaved.Value);
        Assert.Equal(config, File.ReadAllText(paths.NetclawConfigPath));
        Assert.False(File.Exists(paths.SoulPath));
        Assert.Contains("Daemon probe failed", vm.Context.StatusMessage.Value);
        Assert.False(navigation.IsOnboarding);
    }

    [Fact]
    public async Task A_daemon_preparation_failure_remains_visible_without_chat_dispatch()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        using var daemon = new DaemonReadinessFixture(paths);
        var navigation = new ChatNavigationState();
        using var vm = new IdentityRedoViewModel(paths, navigation, daemon.Step);
        for (var index = 0; index < 4; index++) vm.GoNext();
        await vm.OperationCompletion;
        daemon.Failure = new IOException("readiness failed");
        vm.GoNext();
        await vm.OperationCompletion;
        Assert.True(vm.IsSaved.Value);
        Assert.Contains("readiness failed", vm.Context.StatusMessage.Value);
        Assert.False(navigation.IsOnboarding);
    }
}

internal sealed class DaemonReadinessFixture : IDisposable, IHttpClientFactory
{
    private readonly FileStream _lock;
    private readonly HttpClient _http;
    public FakeTimeProvider Clock { get; } = new();
    public HealthCheckStepViewModel Step { get; }
    public bool AdvanceGeneration { get; init; } = true;
    public bool ReportGeneration { get; init; } = true;
    public int Generation { get; set; } = 1;
    public int Probes { get; private set; }
    public string ConfigAtFirstProbe { get; private set; } = "";
    public Exception? Failure { get; set; }

    public DaemonReadinessFixture(NetclawPaths paths)
    {
        _lock = new FileStream(paths.LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _http = new HttpClient(new ResponseHandler(() =>
        {
            if (Failure is { } failure) return Task.FromException<HttpResponseMessage>(failure);
            if (++Probes == 1 && File.Exists(paths.NetclawConfigPath)) ConfigAtFirstProbe = File.ReadAllText(paths.NetclawConfigPath);
            if (AdvanceGeneration) Generation = Probes;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (ReportGeneration) response.Headers.Add("X-Netclaw-Generation", Generation.ToString(CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }));
        Step = new HealthCheckStepViewModel(new DaemonManager(paths, Clock),
            new DaemonApi(this, new ConfigurationBuilder().Build(), paths), timeProvider: Clock);
    }
    public HttpClient CreateClient(string name) => _http;
    public void Dispose() { Step.Dispose(); _http.Dispose(); _lock.Dispose(); }
    private sealed class ResponseHandler(Func<Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response();
    }
}
