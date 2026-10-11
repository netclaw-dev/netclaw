// -----------------------------------------------------------------------
// <copyright file="IdentityRedoPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Headless page-level coverage for <see cref="IdentityRedoPage"/> — the first tests to
/// drive the full identity-redo flow through Termina's real render + input pipeline (the
/// existing <c>IdentityRedoViewModelTests</c> only exercise the ViewModel directly).
///
/// These guard the user-visible symptom of the timezone-submit loop: the flow must walk
/// all four identity sub-steps and reach the saved screen. The loop's root cause was the
/// page failing to clear step-scoped <c>Submitted</c> subscriptions on content rebuild —
/// the documented <c>StepViewCallbacks.Subscriptions</c> contract that the sibling
/// <c>InitWizardPage</c> already honours. The accumulation itself is driven by Termina's
/// cursor-blink re-render timer (wall-clock based) and is covered by the native smoke
/// harness; here we assert the flow reaches "Identity updated" with the four submits
/// mapping 1:1 to the four sub-steps (a double-fire would skip a field; a stuck step
/// never finalizes).
/// </summary>
public sealed class IdentityRedoPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly DaemonReadinessFixture _daemon;

    public IdentityRedoPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _daemon = new DaemonReadinessFixture(_paths);
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() { _daemon.Dispose(); _dir.Dispose(); }

    [Fact]
    public async Task FullFlow_EmptySubmits_AdvancesPastTimezoneToSavedScreen()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // Walk all four identity sub-steps with empty submits — each field falls back to
        // its default (agent name -> "Netclaw", comm style -> first option, user name ->
        // none, timezone -> local). The fourth Enter submits the timezone field, which is
        // the step that previously looped forever instead of reaching the saved screen.
        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        input.EnqueueKey(ConsoleKey.Enter); // timezone -> finalize
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(cts.Token);
        await WaitForTextAsync(terminal, "Identity updated", cts.Token);
        input.EnqueueKey(ConsoleKey.Q, control: true);
        await run;

        Assert.True(vm.IsSaved.Value,
            $"Timezone submit must finalize the redo flow, not loop. Screen:\n{terminal}");
        Assert.True(terminal.Contains("Identity updated"),
            $"Expected the saved screen after the timezone submit. Screen:\n{terminal}");
        Assert.True(File.Exists(_paths.SoulPath), "SOUL.md must be written when the redo finalizes.");
    }

    [Fact]
    public async Task TimezoneSubmit_FinalizesExactlyOnce()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        input.EnqueueKey(ConsoleKey.Enter); // timezone -> finalize
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(cts.Token);
        await WaitForTextAsync(terminal, "Identity updated", cts.Token);
        input.EnqueueKey(ConsoleKey.Q, control: true);
        await run;

        // A stuck/looping timezone step never reaches saved; a double-fire would have
        // skipped past the user-name field. Reaching saved with the local timezone
        // recorded proves the four submits mapped 1:1 to the four sub-steps.
        Assert.True(vm.IsSaved.Value);
        Assert.Equal(IdentityStepViewModel.DefaultTimezone, vm.Step.UserTimezone);
    }

    [Fact]
    public async Task SavedScreen_OffersGuidedChatAndSkip()
    {
        var landing = new ChatLanding();
        var (terminal, app, _) = CreateExistingInstallApp(landing, out var input);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RedoIdentityAsync(app, terminal, input, cts.Token, () => Keys(() => input.EnqueueKey(ConsoleKey.Q, control: true)));

        Assert.True(terminal.Contains("Identity updated."), $"Screen:\n{terminal}");
        Assert.Equal(1, CountOccurrences(terminal.ToString(), "Identity updated."));
        Assert.True(terminal.Contains("Press Enter to start the guided identity chat, or Esc to skip it."),
            $"Screen:\n{terminal}");
        Assert.True(terminal.Contains("[Enter] Start guided identity chat"), $"Screen:\n{terminal}");
        Assert.True(terminal.Contains("[Esc] Skip"), $"Screen:\n{terminal}");
        Assert.False(landing.Entered);
    }

    [Fact]
    public async Task EnterOnSavedScreen_NavigatesToChatWithOnboardingTriggerFromUpdatedIdentity()
    {
        var landing = new ChatLanding();
        var (terminal, app, _) = CreateExistingInstallApp(landing, out var input);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Default completion action: start guided chat.
        await RedoIdentityAsync(app, terminal, input, cts.Token, () => Keys(() => input.EnqueueKey(ConsoleKey.Enter)));

        Assert.True(landing.Entered, "Enter on the saved screen must navigate to chat.");
        Assert.False(string.IsNullOrWhiteSpace(landing.InitialMessage));
        Assert.True(landing.NavigationState.IsOnboarding);

        // Built from the values just entered, not from defaults or stale state.
        Assert.Contains("My name is Pat", landing.InitialMessage);
        Assert.Contains("\"Concise & formal\"", landing.InitialMessage);
        Assert.Contains(_paths.SoulPath, landing.InitialMessage);
        Assert.Contains(_paths.AgentsPath, landing.InitialMessage);

        // The interview still gates the playbook write on operator confirmation.
        Assert.Contains("ask me to confirm it before writing either file", landing.InitialMessage);

        var soul = File.ReadAllText(_paths.SoulPath);
        Assert.Contains("Sentinel", soul);
    }

    [Fact]
    public async Task EscOnSavedScreen_SkipsChatAndExits()
    {
        var landing = new ChatLanding();
        var (terminal, app, _) = CreateExistingInstallApp(landing, out var input);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RedoIdentityAsync(app, terminal, input, cts.Token, () => Keys(() => input.EnqueueKey(ConsoleKey.Escape)));

        Assert.True(File.Exists(_paths.SoulPath), "Skip must not undo the saved identity.");
        Assert.False(landing.Entered, "Skip must not launch chat.");
        Assert.Null(landing.InitialMessage);
        Assert.Null(landing.NavigationState.InitialMessage);
    }

    [Fact]
    public async Task SaveFailure_DoesNotOfferOrLaunchChat()
    {
        // A directory where SOUL.md belongs makes the identity write fail.
        Directory.CreateDirectory(_paths.SoulPath);

        var landing = new ChatLanding();
        var (terminal, app, _) = CreateExistingInstallApp(landing, out var input);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RedoIdentityAsync(app, terminal, input, cts.Token, async () =>
        {
            await WaitForTextAsync(terminal, "Couldn't write SOUL.md: permission denied. Fix it and press Enter to retry.", cts.Token);
            input.EnqueueKey(ConsoleKey.Enter); // would start chat if the save had succeeded
            input.EnqueueKey(ConsoleKey.Q, control: true);
        });

        Assert.False(landing.Entered, "A failed save must not launch chat.");
        Assert.Null(landing.NavigationState.InitialMessage);
        Assert.False(terminal.Contains("Start guided identity chat"), $"Screen:\n{terminal}");
    }

    [Fact]
    public async Task GuidedChatHandoff_ChangesOnlyIdentityInConfig_AndLeavesSecretsUntouched()
    {
        const string config = "{ \"configVersion\": 1, \"Security\": { \"DeploymentPosture\": \"Team\" }, "
                              + "\"Providers\": { \"openrouter\": { \"BaseUrl\": \"https://openrouter.ai/api/v1\" } } }";
        const string secrets = "{ \"Providers\": { \"openrouter\": { \"ApiKey\": \"sk-test-not-real\" } } }";
        File.WriteAllText(_paths.NetclawConfigPath, config);
        File.WriteAllText(_paths.SecretsPath, secrets);

        var landing = new ChatLanding();
        var (terminal, app, _) = CreateExistingInstallApp(landing, out var input);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RedoIdentityAsync(app, terminal, input, cts.Token, () => Keys(() => input.EnqueueKey(ConsoleKey.Enter)));

        Assert.True(landing.Entered);
        var after = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.NotNull(after["Identity"]);
        after.Remove("Identity");
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(config), after));
        Assert.Equal(secrets, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public async Task PartialWrite_NamesTheFailedFile_AndRetrySavesAndStartsChat()
    {
        // SOUL.md is written first; a directory where TOOLING.md belongs fails the second write.
        Directory.CreateDirectory(_paths.ToolingPath);

        var landing = new ChatLanding();
        var (terminal, app, _) = CreateExistingInstallApp(landing, out var input);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RedoIdentityAsync(app, terminal, input, cts.Token, async () =>
        {
            await WaitForTextAsync(terminal, "Couldn't write TOOLING.md", cts.Token);

            Assert.True(File.Exists(_paths.SoulPath), "The first file was written before the failure.");
            Assert.False(landing.Entered);
            Assert.False(terminal.Contains("Start guided identity chat"), $"Screen:\n{terminal}");

            Directory.Delete(_paths.ToolingPath);
            input.EnqueueKey(ConsoleKey.Enter); // retry the save
            await WaitForTextAsync(terminal, "Start guided identity chat", cts.Token);
            Assert.False(terminal.Contains("Couldn't write"), $"Screen:\n{terminal}");

            input.EnqueueKey(ConsoleKey.Enter); // start the guided chat
        });

        Assert.True(landing.Entered);
        Assert.False(string.IsNullOrWhiteSpace(landing.InitialMessage));
        Assert.True(File.Exists(_paths.ToolingPath));
    }

    [Fact]
    public void ChatRoute_IsTheRouteTheHostsRegister()
        => Assert.Equal("/chat", ChatViewModel.Route);

    private static Task Keys(Action enqueue)
    {
        enqueue();
        return Task.CompletedTask;
    }

    internal static async Task WaitForTextAsync(VirtualTerminal terminal, string text, CancellationToken ct)
    {
        while (!terminal.Contains(text))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // Menu (first item: Redo identity setup) -> agent name -> communication style
    // (second option) -> user name -> timezone, then the caller's completion keys.
    // Keys sent while the menu hands over to the redo page would be lost, so the
    // identity keys wait until the redo page is on screen.
    private static async Task RedoIdentityAsync(
        TerminaApplication app,
        VirtualTerminal terminal,
        VirtualInputSource input,
        CancellationToken ct,
        Func<Task> completion)
    {
        var run = app.RunAsync(ct);

        input.EnqueueKey(ConsoleKey.Enter); // menu: Redo identity setup
        while (!terminal.Contains("Agent name"))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        input.EnqueueString("Sentinel");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("Pat");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Enter); // timezone default -> save
        while (!terminal.Contains("Start guided identity chat") && !terminal.Contains("Couldn't write"))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        await completion();

        await run;
    }

    // The existing-install menu, the real redo page, and a stub chat route that records
    // what the real chat page would receive from ChatNavigationState (mirrors the
    // StubChatPage pattern in SessionsPageTests).
    private (VirtualTerminal Terminal, TerminaApplication App, ServiceProvider Services)
        CreateExistingInstallApp(ChatLanding landing, out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddSingleton(landing.NavigationState);
        services.AddTermina(InitExistingInstallViewModel.MenuRoute, builder =>
        {
            builder.RegisterRoute<InitExistingInstallPage, InitExistingInstallViewModel>(
                InitExistingInstallViewModel.MenuRoute,
                _ => new InitExistingInstallPage(),
                _ => new InitExistingInstallViewModel(
                    _paths,
                    new InitNavigationState(),
                    (_, _) => Task.FromResult(new DaemonResult(true, "stopped")),
                    _ => { },
                    new FakeTimeProvider()));
            builder.RegisterRoute<IdentityRedoPage, IdentityRedoViewModel>(
                InitExistingInstallViewModel.IdentityRoute,
                _ => new IdentityRedoPage(),
                _ => new IdentityRedoViewModel(_paths, landing.NavigationState, _daemon.Step));
            builder.RegisterRoute<StubChatPage, StubChatViewModel>(
                ChatViewModel.Route,
                _ => new StubChatPage(),
                _ => new StubChatViewModel(landing));
        });

        var sp = services.BuildServiceProvider();
        return (terminal, sp.GetRequiredService<TerminaApplication>(), sp);
    }

    private sealed class ChatLanding
    {
        public ChatNavigationState NavigationState { get; } = new();
        public bool Entered { get; set; }
        public string? InitialMessage { get; set; }
    }

    private sealed class StubChatViewModel(ChatLanding landing) : ReactiveViewModel
    {
        public override void OnActivated()
        {
            base.OnActivated();
            landing.Entered = true;
            landing.InitialMessage = landing.NavigationState.TakeInitialMessage();
            Shutdown();
        }
    }

    private sealed class StubChatPage : ReactivePage<StubChatViewModel>
    {
        public override ILayoutNode BuildLayout() => Layouts.Empty();
    }

    private (VirtualTerminal Terminal, TerminaApplication App, IdentityRedoViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
        => HeadlessTerminaFixture.Create<IdentityRedoPage, IdentityRedoViewModel>(
            "/identity-redo",
            _ => new IdentityRedoPage(),
            () => new IdentityRedoViewModel(_paths, new ChatNavigationState(), _daemon.Step),
            out input);
}
