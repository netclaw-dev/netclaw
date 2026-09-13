// -----------------------------------------------------------------------
// <copyright file="McpToolPermissionsPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Termina;
using Termina.Input;
using Termina.Rendering;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpToolPermissionsPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public McpToolPermissionsPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

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
        AssertLineHasBackground(terminal, "Server default", Color.Cyan);
        AssertLineDoesNotHaveBackground(terminal, "Audience", Color.Cyan);
        AssertLineDoesNotHaveBackground(terminal, "Server enabled", Color.Cyan);
        AssertLineDoesNotHaveBackground(terminal, "create-pages", Color.Cyan);
        AssertLineDoesNotHaveBackground(terminal, "search", Color.Cyan);
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
    public async Task ToolGrid_SpaceOnServerEnabledRow_TogglesServerAccess()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        var wasBefore = vm.IsServerAllowedForSelectedAudience();

        // Navigate to row 1 (Server enabled), Space toggles.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Spacebar);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotEqual(wasBefore, vm.IsServerAllowedForSelectedAudience());
    }

    [Fact]
    public async Task ToolGrid_SpaceOnToolRow_TogglesTool()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages", "search"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Enable server access first so tool toggling works
        if (!vm.IsServerAllowedForSelectedAudience())
            vm.ToggleServerAccess();

        var wasBefore = vm.IsToolGranted(new ToolName("create-pages"));

        // Navigate to row 3 (first tool), Space toggles grant.
        input.EnqueueKey(ConsoleKey.DownArrow); // row 1
        input.EnqueueKey(ConsoleKey.DownArrow); // row 2
        input.EnqueueKey(ConsoleKey.DownArrow); // row 3 (first tool)
        input.EnqueueKey(ConsoleKey.Spacebar);
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
    public async Task ToolGrid_EnterThenY_SavesAndGoesBack()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Make a change (toggle server access via Space), then Enter → Y to save.
        input.EnqueueKey(ConsoleKey.DownArrow); // row 1 (server enabled)
        input.EnqueueKey(ConsoleKey.Spacebar);  // toggle → creates unsaved state
        input.EnqueueKey(ConsoleKey.Enter);     // triggers confirmation
        input.EnqueueKey(ConsoleKey.Y);         // confirm save → goes back to server list
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(ToolPermissionsState.ServerList, vm.CurrentState.Value);
    }

    [Fact]
    public async Task ToolGrid_EnterThenEnter_SavesDefaultAndGoesBack()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Spacebar);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(ToolPermissionsState.ServerList, vm.CurrentState.Value);
    }

    [Fact]
    public async Task ToolGrid_EnterThenN_DiscardsAndGoesBack()

    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Make a change, then Enter → N to discard.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Spacebar);  // toggle → unsaved
        input.EnqueueKey(ConsoleKey.Enter);     // triggers confirmation
        input.EnqueueKey(ConsoleKey.N);         // discard → goes back
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(ToolPermissionsState.ServerList, vm.CurrentState.Value);
    }

    [Fact]
    public async Task ToolGrid_EnterThenEsc_CancelsConfirmation()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.InitializeForTests(new McpServerName("notion"), ["create-pages"]);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Make a change, then Enter → Esc to cancel confirmation and stay.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Spacebar);  // toggle → unsaved
        input.EnqueueKey(ConsoleKey.Enter);     // triggers confirmation
        input.EnqueueKey(ConsoleKey.Escape);    // cancel → stays on tool grid
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(ToolPermissionsState.ToolGrid, vm.CurrentState.Value);
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

    [Fact]
    public async Task ToolGrid_ManyTools_HeaderRowsNotOverwrittenByScrollContent()
    {
        // Regression test for issue #1424: ScrollableContainerNode.Render now correctly
        // respects bounds, so it no longer overwrites header rows when many tools are
        // displayed. The PanelNode workaround was removed in issue #1427.
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // 15 tools: on a 40-row terminal the tool list starts at row ~10 without the
        // bug fix; with the bug it writes at row 0 and overwrites every header row.
        var tools = Enumerable.Range(1, 15).Select(i => $"tool-{i:00}").ToList();
        vm.InitializeForTests(new McpServerName("notion"), tools);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("MCP Permissions"),
            $"Expected page header. Screen:\n{terminal}");
        Assert.True(terminal.Contains("Audience"),
            $"Expected 'Audience' row not overwritten by tool list. Screen:\n{terminal}");
        Assert.True(terminal.Contains("Server default"),
            $"Expected 'Server default' row not overwritten by tool list. Screen:\n{terminal}");
    }

    [Fact]
    public async Task ToolGrid_RightArrowOnScrolledToolRow_PreservesScrollPosition()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input, height: 18);

        var tools = Enumerable.Range(1, 50)
            .Select(i => $"tool-{i:00}")
            .ToList();

        vm.InitializeForTests(new McpServerName("notion"), tools);
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        if (!vm.IsServerAllowedForSelectedAudience())
            vm.ToggleServerAccess();

        // Cursor starts at row 0. Move to row 38:
        // row 0 = Audience, row 1 = Server enabled, row 2 = Server default,
        // row 3 = tool-01, so row 38 = tool-36.
        for (var i = 0; i < 38; i++)
            input.EnqueueKey(ConsoleKey.DownArrow);

        input.EnqueueKey(ConsoleKey.RightArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("tool-36"),
            $"Expected scrolled/focused row to remain visible after RightArrow. Screen:\n{terminal}");
        Assert.False(terminal.Contains("tool-01"),
            $"Expected scroll not to reset to the top after RightArrow. Screen:\n{terminal}");
        AssertLineHasBackground(terminal, "tool-36", Color.Cyan);

        var (mode, inherited) = vm.GetEffectiveMode(new ToolName("tool-36"));
        Assert.Equal(ToolApprovalMode.Auto, mode);
        Assert.False(inherited);
    }

    [Fact]
    public async Task Loading_Escape_QuitsInsteadOfStalling()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.Escape);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(ToolPermissionsState.Loading, vm.CurrentState.Value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (VirtualTerminal Terminal, TerminaApplication App, McpToolPermissionsViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input, int width = 120, int height = 40)
    {
        var configuration = new ConfigurationBuilder().Build();
        var daemonApi = new DaemonApi(new FailingHttpClientFactory(), configuration, _paths);

        return HeadlessTerminaFixture.Create<McpToolPermissionsPage, McpToolPermissionsViewModel>(
            "/mcp-tools",
            () => new McpToolPermissionsPage(),
            () => new McpToolPermissionsViewModel(_paths, daemonApi),
            out input,
            width,
            height);
    }

    private static void AssertLineHasBackground(VirtualTerminal terminal, string text, Color expected)
    {
        var row = FindLine(terminal, text);
        var hasExpectedBackground = Enumerable.Range(0, terminal.Width)
            .Any(column => terminal.GetBackground(column, row) == expected);

        Assert.True(hasExpectedBackground,
            $"Expected line containing '{text}' to include {expected} background. Screen:\n{terminal}");
    }

    private static void AssertLineDoesNotHaveBackground(VirtualTerminal terminal, string text, Color unexpected)
    {
        var row = FindLine(terminal, text);
        var hasUnexpectedBackground = Enumerable.Range(0, terminal.Width)
            .Any(column => terminal.GetBackground(column, row) == unexpected);

        Assert.False(hasUnexpectedBackground,
            $"Expected line containing '{text}' not to include {unexpected} background. Screen:\n{terminal}");
    }

    private static int FindLine(VirtualTerminal terminal, string text)
    {
        var lines = terminal.GetAllLines();
        var row = Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));
        Assert.True(row >= 0, $"Expected line containing '{text}'. Screen:\n{terminal}");
        return row;
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
