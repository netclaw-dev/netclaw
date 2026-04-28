using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Tools;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpToolPermissionsPageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public McpToolPermissionsPageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-mcppage-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ToolGrid_UpDown_NavigatesAllRows()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages", "search"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Start at row 0 (Audience). Down 4 times → row 4 (second tool).
        // Then Up twice → row 2 (Server default). Then Ctrl+Q.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.UpArrow);
        input.EnqueueKey(ConsoleKey.UpArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Audience"),
            $"Expected 'Audience' in terminal. Screen:\n{terminal}");
        Assert.True(terminal.Contains("Server default"),
            $"Expected 'Server default' in terminal. Screen:\n{terminal}");
        Assert.True(terminal.Contains("create-pages"),
            $"Expected 'create-pages' in terminal. Screen:\n{terminal}");
        Assert.True(terminal.Contains("search"),
            $"Expected 'search' in terminal. Screen:\n{terminal}");
    }

    [Fact]
    public async Task ToolGrid_RightArrowOnAudienceRow_CyclesAudience()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Cursor starts at row 0 (Audience). Right arrow cycles Personal → Team.
        input.EnqueueKey(ConsoleKey.RightArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(TrustAudience.Team, vm.SelectedAudience);
    }

    [Fact]
    public async Task ToolGrid_LeftArrowOnAudienceRow_CyclesAudienceBack()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Left arrow on audience row cycles Personal → Public (backward).
        input.EnqueueKey(ConsoleKey.LeftArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(TrustAudience.Public, vm.SelectedAudience);
    }

    [Fact]
    public async Task ToolGrid_RightArrowOnServerDefaultRow_CyclesServerDefault()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Navigate to row 2 (Server default), then Right to cycle Auto → Approval.
        input.EnqueueKey(ConsoleKey.DownArrow); // row 1
        input.EnqueueKey(ConsoleKey.DownArrow); // row 2 (Server default)
        input.EnqueueKey(ConsoleKey.RightArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(ToolApprovalMode.Approval, vm.GetServerDefault());
    }

    [Fact]
    public async Task ToolGrid_LeftArrowOnServerDefaultRow_CyclesServerDefaultBack()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Navigate to row 2, then Left to cycle Auto → Deny (backward).
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.LeftArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(ToolApprovalMode.Deny, vm.GetServerDefault());
    }

    [Fact]
    public async Task ToolGrid_EnterOnServerEnabledRow_TogglesServerAccess()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        var wasBefore = vm.IsServerAllowedForSelectedAudience();

        // Navigate to row 1 (Server enabled), Enter toggles.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotEqual(wasBefore, vm.IsServerAllowedForSelectedAudience());
    }

    [Fact]
    public async Task ToolGrid_EnterOnToolRow_TogglesTool()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages", "search"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Enable server access first so tool toggling works
        if (!vm.IsServerAllowedForSelectedAudience())
            vm.ToggleServerAccess();

        var wasBefore = vm.IsToolGranted(new ToolName("create-pages"));

        // Navigate to row 3 (first tool), Enter toggles grant.
        input.EnqueueKey(ConsoleKey.DownArrow); // row 1
        input.EnqueueKey(ConsoleKey.DownArrow); // row 2
        input.EnqueueKey(ConsoleKey.DownArrow); // row 3 (first tool)
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotEqual(wasBefore, vm.IsToolGranted(new ToolName("create-pages")));
    }

    [Fact]
    public async Task ToolGrid_RightArrowOnToolRow_CyclesToolOverride()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        if (!vm.IsServerAllowedForSelectedAudience())
            vm.ToggleServerAccess();

        // Navigate to row 3 (first tool), Right cycles inherit → Auto.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.RightArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var (mode, inherited) = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.Equal(ToolApprovalMode.Auto, mode);
        Assert.False(inherited);
    }

    [Fact]
    public async Task ToolGrid_SaveClearsUnsavedState()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Make a change (toggle server access) then save.
        input.EnqueueKey(ConsoleKey.DownArrow); // row 1 (server enabled)
        input.EnqueueKey(ConsoleKey.Enter);     // toggle → creates unsaved state
        input.EnqueueKey(ConsoleKey.S);         // save
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ToolGrid_RightArrowOnServerEnabledRow_TogglesServerAccess()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        var wasBefore = vm.IsServerAllowedForSelectedAudience();

        // Navigate to row 1, Right arrow toggles (same as Enter on this row).
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.RightArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotEqual(wasBefore, vm.IsServerAllowedForSelectedAudience());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (VirtualTerminal Terminal, TerminaApplication App, McpToolPermissionsViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        McpToolPermissionsViewModel? capturedVm = null;

        var configuration = new ConfigurationBuilder().Build();
        var daemonPaths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-daemon-{Guid.NewGuid():N}"));
        daemonPaths.EnsureDirectoriesExist();
        var daemonApi = new DaemonApi(new FailingHttpClientFactory(), configuration, daemonPaths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/mcp-tools", builder =>
        {
            builder.RegisterRoute<McpToolPermissionsPage, McpToolPermissionsViewModel>(
                "/mcp-tools",
                _ => new McpToolPermissionsPage(),
                _ =>
                {
                    capturedVm = new McpToolPermissionsViewModel(_paths, daemonApi);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Test: no daemon available");
    }

    private sealed class FailingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailingHttpHandler());
    }
}
