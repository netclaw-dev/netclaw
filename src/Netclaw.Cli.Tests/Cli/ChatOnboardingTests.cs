// -----------------------------------------------------------------------
// <copyright file="ChatOnboardingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using R3;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// <c>netclaw chat --onboarding</c> must hand chat the same trigger the wizard and the redo
/// flow build, as the first turn of a new interactive session.
/// </summary>
public sealed class ChatOnboardingTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ChatOnboardingTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void BuildTrigger_is_the_wizard_trigger_for_the_saved_identity()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """{ "Identity": { "AgentName": "Rex", "UserName": "Pat", "CommunicationStyle": "Concise & formal" } }""");

        using var step = new IdentityStepViewModel { UserName = "Pat", CommunicationStyle = "Concise & formal" };

        Assert.Equal(step.BuildOnboardingTrigger(_paths), ChatOnboarding.BuildTrigger(_paths));
    }

    [Fact]
    public void BuildTrigger_without_saved_identity_uses_the_wizard_defaults()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");

        using var step = new IdentityStepViewModel();

        Assert.Equal(step.BuildOnboardingTrigger(_paths), ChatOnboarding.BuildTrigger(_paths));
    }

    // Plain `netclaw chat` and the daemon open a netclaw.json with comments and trailing commas.
    [Fact]
    public void BuildTrigger_on_a_config_with_comments_uses_the_wizard_defaults()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\n  // my notes\n  \"Identity\": { \"UserName\": \"Pat\", },\n}");

        using var step = new IdentityStepViewModel();

        Assert.Equal(step.BuildOnboardingTrigger(_paths), ChatOnboarding.BuildTrigger(_paths));
    }

    [Fact]
    public void Trigger_has_the_paths_and_asks_for_confirmation_before_writing()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");

        var trigger = ChatOnboarding.BuildTrigger(_paths);

        Assert.Contains(_paths.SoulPath, trigger);
        Assert.Contains(_paths.AgentsPath, trigger);
        Assert.Contains("ask me to confirm it before writing either file", trigger);
        // A second interview must update the existing files, not replace them.
        Assert.Contains("use file_read on both files, preserve their existing structure", trigger);
    }

    [Fact]
    public void ValidateCombination_allows_a_plain_interactive_chat()
        => Assert.Null(ChatOnboarding.ValidateCombination(headless: false, resumeSessionId: null));

    [Fact]
    public void ValidateCombination_rejects_headless()
    {
        var error = ChatOnboarding.ValidateCombination(headless: true, resumeSessionId: null);

        Assert.NotNull(error);
        Assert.Contains("-p", error);
    }

    [Fact]
    public void ValidateCombination_rejects_resume()
    {
        var error = ChatOnboarding.ValidateCombination(headless: false, resumeSessionId: "abc123");

        Assert.NotNull(error);
        Assert.Contains("--resume", error);
    }

    [Fact]
    public void ValidateToken_rejects_a_value_on_the_flag()
    {
        Assert.NotNull(ChatOnboarding.ValidateToken("--onboarding=true"));
        Assert.NotNull(ChatOnboarding.ValidateToken("--onboarding="));
        Assert.Null(ChatOnboarding.ValidateToken("--onboarding"));
        Assert.Null(ChatOnboarding.ValidateToken("--resume=abc"));
    }

    [Fact]
    public void TakeInitialMessage_clears_the_message()
    {
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding("trigger");

        Assert.Equal("trigger", navigation.TakeInitialMessage());
        Assert.Null(navigation.TakeInitialMessage());
    }

    [Fact]
    public void Next_steps_name_the_onboarding_chat_only_after_a_finish_with_warnings()
    {
        Assert.Contains("netclaw chat --onboarding", HealthCheckStepView.ChatNextStep(succeeded: false));
        Assert.DoesNotContain("--onboarding", HealthCheckStepView.ChatNextStep(succeeded: true));
    }

    [Fact]
    public async Task Trigger_is_sent_once_across_a_further_turn_and_a_reconnect()
    {
        var trigger = "onboarding-trigger";
        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding(trigger);

        var turns = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var turnSent = new SemaphoreSlim(0);

        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = (method, args, _) =>
            {
                if (method == "SendMessage" && args.Length > 1 && args[1] is string text)
                {
                    turns.Enqueue(text);
                    turnSent.Release();
                }
                return Task.CompletedTask;
            }
        };
        await using var client = new DaemonClient(
            "http://localhost", transport, reconnectDelays: [TimeSpan.Zero], rpcTimeout: TimeSpan.FromSeconds(30));

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State is DaemonConnectionState.Connected && evt.Message.Contains("Reconnected", StringComparison.Ordinal))
                reconnected.TrySetResult();
        });

        using var chat = new ChatViewModel(
            client, TimeProvider.System, new ModelCapabilities { ModelId = "test-model" }, navigation, _paths);

        chat.OnActivated();
        Assert.True(await turnSent.WaitAsync(timeout, ct), "The onboarding trigger was never sent.");

        await chat.SubmitAsync("hello");
        Assert.True(await turnSent.WaitAsync(timeout, ct), "The turn after the trigger was never sent.");

        transport.RaiseClosed();
        await reconnected.Task.WaitAsync(timeout, ct);

        await chat.SubmitAsync("after-reconnect");

        Assert.True(await turnSent.WaitAsync(timeout, ct), "The turn submitted after the reconnect was never sent.");

        // A trigger re-sent by the flush after the reconnect would land in the list ahead of this turn.
        await chat.SubmitAsync("sentinel");
        Assert.True(await turnSent.WaitAsync(timeout, ct), "The closing turn was never sent.");

        Assert.Equal([trigger, "hello", "after-reconnect", "sentinel"], turns.ToArray());
    }

    [Fact]
    public void DaemonUnavailableHint_points_an_onboarding_chat_at_the_flag()
    {
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding("trigger");

        var hint = ChatOnboarding.DaemonUnavailableHint(navigation);

        Assert.NotNull(hint);
        Assert.Contains("netclaw chat --onboarding", hint);
    }

    [Fact]
    public void DaemonUnavailableHint_is_silent_for_other_chats()
    {
        Assert.Null(ChatOnboarding.DaemonUnavailableHint(new ChatNavigationState()));
        Assert.Null(ChatOnboarding.DaemonUnavailableHint(new ChatNavigationState { InitialMessage = "/run-reminder x" }));
        Assert.Null(ChatOnboarding.DaemonUnavailableHint(null));
    }

    [Fact]
    public async Task Chat_sends_the_trigger_as_the_first_turn()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        var trigger = ChatOnboarding.BuildTrigger(_paths);
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding(trigger);

        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = (method, args, _) =>
            {
                if (method == "SendMessage" && args.Length > 1 && args[1] is string text)
                    sent.TrySetResult(text);
                return Task.CompletedTask;
            }
        };
        await using var client = new DaemonClient(
            "http://localhost",
            transport,
            reconnectDelays: [TimeSpan.Zero],
            rpcTimeout: TimeSpan.FromSeconds(5));
        using var chat = new ChatViewModel(
            client,
            TimeProvider.System,
            new ModelCapabilities { ModelId = "test-model" },
            navigation,
            _paths);

        chat.OnActivated();

        var firstTurn = await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(trigger, firstTurn);
        Assert.True(navigation.IsOnboarding);
    }

    // ── The built CLI as a child process ──

    [Theory]
    [InlineData("chat --onboarding -p x", "cannot be combined with -p")]
    [InlineData("chat --onboarding --resume abc", "cannot be combined with --resume")]
    [InlineData("chat --onboarding=true", "--onboarding takes no value")]
    public async Task Cli_rejects_bad_onboarding_combinations(string args, string expected)
    {
        var (exitCode, output) = await RunCliAsync(args, usePty: false);

        Assert.Equal(1, exitCode);
        Assert.Contains(expected, output);
    }

    [Fact]
    public async Task Cli_onboarding_chat_without_a_daemon_exits_1_and_names_the_flag()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists(ScriptPath), "needs util-linux script(1) to give the TUI a pty");
        WriteConfiguredHome();

        var (exitCode, output) = await RunCliAsync("chat --onboarding", usePty: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("Could not reach the Netclaw daemon", output);
        Assert.Contains("netclaw chat --onboarding", output);
    }

    [Fact]
    public async Task Cli_plain_chat_without_a_daemon_exits_1_without_the_hint()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists(ScriptPath), "needs util-linux script(1) to give the TUI a pty");
        WriteConfiguredHome();

        var (exitCode, output) = await RunCliAsync("chat", usePty: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("Could not reach the Netclaw daemon", output);
        Assert.DoesNotContain("--onboarding", output);
    }

    private const string ScriptPath = "/usr/bin/script";

    // A provider and main model with no pinned context window, so chat probes the daemon
    // at startup (and fails fast when it is down) instead of waiting on a configured value.
    private void WriteConfiguredHome()
        => File.WriteAllText(_paths.NetclawConfigPath,
            """
            {"configVersion":1,"Providers":{"local":{"Type":"openai-compatible","BaseUrl":"http://127.0.0.1:9","Models":["m"]}},"Models":{"Main":{"Provider":"local","ModelId":"m"}}}
            """);

    private async Task<(int ExitCode, string Output)> RunCliAsync(string args, bool usePty)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "netclaw.dll");
        var command = $"dotnet \"{dll}\" {args}";
        var info = usePty
            ? new ProcessStartInfo(ScriptPath, ["-qec", command, "/dev/null"])
            : new ProcessStartInfo("dotnet", [dll, .. args.Split(' ')]);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        info.UseShellExecute = false;
        info.Environment["NETCLAW_HOME"] = _dir.Path;
        info.Environment["NETCLAW_DAEMON_ENDPOINT"] = "http://127.0.0.1:1";

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
}
