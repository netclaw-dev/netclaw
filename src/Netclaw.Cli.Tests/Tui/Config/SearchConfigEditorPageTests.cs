// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http;
using System.Text;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SearchConfigEditorPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SearchConfigEditorPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Search": {
                "Backend": "duckduckgo"
              }
            }
            """);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ProviderSelection_RendersActiveCheckboxAndConfiguredLegend()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("[x] active backend"),
            $"Expected active-backend legend in terminal output. Screen:\n{terminal}");
        Assert.True(terminal.Contains("[x] DuckDuckGo"),
            $"Expected active backend checkbox in terminal output. Screen:\n{terminal}");
        Assert.True(terminal.Contains("backend has saved setup"),
            $"Expected configured-backend legend in terminal output. Screen:\n{terminal}");
    }

    [Fact]
    public async Task SavedScreen_EscapeReturnsToProviderSelection()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        vm.SaveWithoutProbeOverride();

        input.EnqueueKey(ConsoleKey.Escape);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SearchConfigEditorScreen.ProviderSelection, vm.CurrentScreen.Value);
        Assert.True(terminal.Contains("Choose the backend Netclaw uses for web search."),
            $"Expected provider selection screen after Esc from saved state. Screen:\n{terminal}");
    }

    [Fact]
    public async Task BraveEntry_AcceptsTypedAndPastedApiKeyInput()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"web\":{\"results\":[]}}", Encoding.UTF8, "application/json"),
            }));

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("BSA-");
        input.EnqueuePaste("pasted-key");
        input.EnqueueKey(ConsoleKey.LeftArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("BSA-pasted-key", vm.FieldValues["Search.BraveApiKey"].Value);
    }

    private (VirtualTerminal Terminal, TerminaApplication App, SearchConfigEditorViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input, IHttpClientFactory? httpClientFactory = null)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        SearchConfigEditorViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/search", builder =>
        {
            builder.RegisterRoute<SearchConfigEditorPage, SearchConfigEditorViewModel>(
                "/search",
                _ => new SearchConfigEditorPage(),
                _ =>
                {
                    capturedVm = new SearchConfigEditorViewModel(_paths, httpClientFactory);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(handler));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
