// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using System.Text;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SearchConfigEditorViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SearchConfigEditorViewModelTests()
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
    public void Search_dashboard_entry_routes_to_real_editor()
    {
        using var vm = new Netclaw.Cli.Tui.ConfigDashboardViewModel(new Netclaw.Cli.Tui.ConfigDashboardNavigationState());
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.Activate(vm.Items.Single(static item => item.Label == "Search"));

        Assert.Equal("/search", route);
    }

    [Fact]
    public void Fields_project_search_enabled_out_of_editor()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        Assert.DoesNotContain(vm.Fields, static field => field.Path == "Search.Enabled");
        Assert.Contains(vm.Fields, static field => field.Path == "Search.Backend");
    }

    [Fact]
    public async Task Brave_probe_failure_opens_override_dialog_before_save()
    {
        using var vm = new SearchConfigEditorViewModel(_paths, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        vm.SetFieldValue("Search.Backend", "brave");
        vm.SetFieldValue("Search.BraveApiKey", "bad-key");

        await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SearchConfigEditorDialog.ProbeWarning, vm.ActiveDialog.Value);
        Assert.Contains("authentication failed", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_anyway_persists_config_and_secret_semantically()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SetFieldValue("Search.Backend", "brave");
        vm.SetFieldValue("Search.BraveApiKey", "BSA-live-key");
        vm.SaveWithoutProbeOverride();

        var config = File.ReadAllText(_paths.NetclawConfigPath);
        var secrets = File.ReadAllText(_paths.SecretsPath);

        Assert.Contains("\"Backend\": \"brave\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("BraveApiKey", config, StringComparison.Ordinal);
        Assert.Contains("BraveApiKey", secrets, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_secret_preserves_existing_secret()
    {
        var protector = SecretsProtection.CreateProtector(_paths);
        var encrypted = protector.Protect("stored-secret");
        File.WriteAllText(_paths.SecretsPath,
            "{\n" +
            "  \"configVersion\": 1,\n" +
            "  \"Search\": {\n" +
            $"    \"BraveApiKey\": \"{encrypted}\"\n" +
            "  }\n" +
            "}\n");

        using var vm = new SearchConfigEditorViewModel(_paths);
        vm.SetFieldValue("Search.Backend", "brave");
        vm.SetFieldValue("Search.BraveApiKey", "");
        vm.SaveWithoutProbeOverride();

        var secrets = File.ReadAllText(_paths.SecretsPath);
        Assert.Contains(encrypted, secrets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_probe_allows_save_without_dialog()
    {
        using var vm = new SearchConfigEditorViewModel(_paths, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"web\":{\"results\":[]}}", Encoding.UTF8, "application/json"),
            }));

        vm.SetFieldValue("Search.Backend", "brave");
        vm.SetFieldValue("Search.BraveApiKey", "good-key");

        await vm.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SearchConfigEditorDialog.None, vm.ActiveDialog.Value);
        Assert.Contains("Saved Search settings", vm.Status.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_backend_specific_fields_raise_structural_errors()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SetFieldValue("Search.Backend", "searxng");
        vm.SetFieldValue("Search.SearXngEndpoint", "");

        var issues = vm.ValidationSummary.Value.IssuesFor("Search.SearXngEndpoint");
        Assert.Contains(issues, static issue => issue.Message.Contains("requires an endpoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Preserved_state_supports_in_memory_draft_edits()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SetFieldValue("Search.Backend", "searxng");
        vm.SetFieldValue("Search.SearXngEndpoint", "https://search.example.com");
        vm.OnDeactivating();
        vm.OnActivated();

        Assert.Equal("searxng", vm.FieldValues["Search.Backend"].Value);
        Assert.Equal("https://search.example.com", vm.FieldValues["Search.SearXngEndpoint"].Value);
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
