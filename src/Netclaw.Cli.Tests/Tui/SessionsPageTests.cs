// -----------------------------------------------------------------------
// <copyright file="SessionsPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
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
/// Regression tests for <see cref="SessionsPage"/> / <see cref="SessionsViewModel"/>.
///
/// Guards the input wiring for the scrollable session list. The list is rendered as a
/// focusable <c>SelectionListNode</c> with <c>.WithHighlightedIndex()</c> (a one-way bind
/// FROM the ViewModel). Because the node is focusable, Termina's focus manager would route
/// arrows + Enter straight into it (its <c>HandleInput</c> consumes them), so the page MUST
/// claim those keys in <see cref="SessionsPage.HandlePageInput"/> — which Termina dispatches
/// before the focus manager — and forward them to <see cref="SessionsViewModel.HandleKey"/>.
/// Remove that override and these tests fail: arrows no longer reach the ViewModel,
/// <see cref="SessionsViewModel.SelectedIndex"/> sticks at 0, and Enter always resumes the
/// first session — the original bug.
///
/// Scope note: these drive input through the real <c>HandlePageInput</c> path, so they guard
/// the page→ViewModel wiring. They do NOT reproduce the focus-manager-eats-input condition
/// itself — that needs a populated list that has actually been handed focus in a live
/// terminal, which the headless harness does not do during input processing (it was the
/// blind spot that let the bug ship). Native/manual coverage owns that last mile.
///
/// Resume state is observed via the shared <see cref="ChatNavigationState"/>, which is
/// what the ViewModel actually writes (<c>ResumeSessionId</c> lives there, not on the
/// ViewModel). <see cref="SessionCatalogEntryDto.SessionId"/> strips the <c>session-</c>
/// prefix, so a persistence id of <c>session-003</c> resumes as <c>003</c>.
/// </summary>
public sealed class SessionsPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero));

    public SessionsPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    private static SessionCatalogEntryDto CreateSession(
        string persistenceId, string channel, int turnCount, DateTimeOffset lastActivity)
    {
        return new SessionCatalogEntryDto
        {
            PersistenceId = persistenceId,
            Title = persistenceId.Replace("session-", "").Replace("-", " "),
            Channel = channel,
            TurnCount = turnCount,
            Status = "active",
            CreatedAt = lastActivity.ToUnixTimeMilliseconds(),
            LastActivity = lastActivity.ToUnixTimeMilliseconds()
        };
    }

    [Fact]
    public async Task EmptyCatalog_RendersEmptyState()
    {
        var (terminal, app, _, _) = CreateHeadlessApp(out var input, []);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Sessions"), "Expected page title 'Sessions'");
        Assert.True(terminal.Contains("No sessions found"), "Expected empty-state message");
    }

    [Fact]
    public async Task SessionsList_RendersEntries()
    {
        var sessions = new[]
        {
            CreateSession("session-abc-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-def-002", "tui", 2, _time.GetUtcNow().AddMinutes(-10)),
        };

        var (terminal, app, _, _) = CreateHeadlessApp(out var input, sessions);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("tui"), $"Expected channel name 'tui'. Screen:\n{terminal}");
    }

    [Fact]
    public async Task DownArrow_AdvancesSelectedIndex_AndClampsAtEnd()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
            CreateSession("session-003", "tui", 3, _time.GetUtcNow().AddMinutes(-3)),
        };

        var (_, app, vm, _) = CreateHeadlessApp(out var input, sessions);

        // Four downs against three sessions: index should advance then clamp at 2.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(2, vm.SelectedIndex.Value);
    }

    [Fact]
    public async Task UpArrow_MovesSelectedIndexBack()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
            CreateSession("session-003", "tui", 3, _time.GetUtcNow().AddMinutes(-3)),
        };

        var (_, app, vm, _) = CreateHeadlessApp(out var input, sessions);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.UpArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(1, vm.SelectedIndex.Value);
    }

    [Fact]
    public async Task DownArrow_VisiblyHighlightsSelectedRow()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
            CreateSession("session-003", "tui", 3, _time.GetUtcNow().AddMinutes(-3)),
        };

        var (terminal, app, vm, _) = CreateHeadlessApp(out var input, sessions);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(1, vm.SelectedIndex.Value);
        AssertLineHasBackground(terminal, "002", Color.Cyan);
        AssertLineDoesNotHaveBackground(terminal, "001", Color.Cyan);
    }

    [Fact]
    public async Task EnterOnSelectedSession_ResumesThatSession_NotTheFirst()
    {
        // THE key regression: arrow keys must drive SelectedIndex so Enter resumes the
        // highlighted row. If arrows stop reaching the ViewModel, this resumes session 0.
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
            CreateSession("session-003", "tui", 3, _time.GetUtcNow().AddMinutes(-3)),
        };

        var (_, app, vm, nav) = CreateHeadlessApp(out var input, sessions);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(2, vm.SelectedIndex.Value);
        // SessionId strips the "session-" prefix.
        Assert.Equal("003", nav.ResumeSessionId);
    }

    [Fact]
    public async Task NKey_StartsNewChat_WithoutResuming()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
        };

        var (_, app, _, nav) = CreateHeadlessApp(out var input, sessions);

        input.EnqueueKey(ConsoleKey.N);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Null(nav.ResumeSessionId);
    }

    [Fact]
    public async Task LongList_RendersManyRows_ForScrollableList()
    {
        var sessions = Enumerable.Range(1, 100)
            .Select(i => CreateSession($"session-{i:000}", "tui", i, _time.GetUtcNow().AddMinutes(-i)))
            .ToArray();

        var (terminal, app, _, _) = CreateHeadlessApp(out var input, sessions);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        var visibleCount = Enumerable.Range(1, 100)
            .Count(i => screen.Contains($"{i:000}", StringComparison.Ordinal));

        Assert.True(visibleCount > 15,
            $"Expected >15 sessions visible in scrollable list; only {visibleCount} visible. Screen:\n{terminal}");
    }

    [Fact]
    public async Task LongList_DownArrowScrollsSelectedRowIntoView_WithHighlight()
    {
        var sessions = Enumerable.Range(1, 100)
            .Select(i => CreateSession($"session-{i:000}", "tui", i, _time.GetUtcNow().AddMinutes(-i)))
            .ToArray();

        var (terminal, app, vm, _) = CreateHeadlessApp(out var input, sessions);
        for (var i = 0; i < 49; i++)
            input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(49, vm.SelectedIndex.Value);
        AssertLineHasBackground(terminal, "050", Color.Cyan);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (VirtualTerminal Terminal, TerminaApplication App, SessionsViewModel Vm, ChatNavigationState Nav)
        CreateHeadlessApp(out VirtualInputSource input, SessionCatalogEntryDto[] sessions)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        SessionsViewModel? capturedVm = null;
        var nav = new ChatNavigationState();

        var configuration = new ConfigurationBuilder().Build();
        var daemonApi = new DaemonApi(new MockHttpClientFactory(new MockSessionsHttpHandler(sessions)), configuration, _paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/sessions", builder =>
        {
            builder.RegisterRoute<SessionsPage, SessionsViewModel>(
                "/sessions",
                _ => new SessionsPage(),
                _ =>
                {
                    capturedVm = new SessionsViewModel(daemonApi, nav, _time);
                    return capturedVm;
                });

            // Landing route for the Enter/N resume paths: the ViewModel navigates to
            // "/chat" after setting resume state. The stub terminates immediately so the
            // app loop exits without waiting on the cancellation timeout.
            builder.RegisterRoute<StubChatPage, StubChatViewModel>(
                "/chat",
                _ => new StubChatPage(),
                _ => new StubChatViewModel());
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!, nav);
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

    private sealed class MockSessionsHttpHandler : HttpMessageHandler
    {
        private readonly SessionCatalogEntryDto[] _sessions;

        public MockSessionsHttpHandler(SessionCatalogEntryDto[] sessions) => _sessions = sessions;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_sessions);

            // Ownership of the response transfers to the calling HttpClient, which disposes it.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public MockHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler);
    }

    private sealed class StubChatViewModel : ReactiveViewModel
    {
        public override void OnActivated()
        {
            base.OnActivated();
            Shutdown();
        }
    }

    private sealed class StubChatPage : ReactivePage<StubChatViewModel>
    {
        public override ILayoutNode BuildLayout() => Layouts.Empty();
    }
}
