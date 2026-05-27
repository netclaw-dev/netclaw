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
    public void Starts_on_provider_selection_screen()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        Assert.Equal(SearchConfigEditorScreen.ProviderSelection, vm.CurrentScreen.Value);
        Assert.Equal("duckduckgo", vm.CurrentBackendValue);
        Assert.Null(vm.CurrentProviderField);
    }

    [Fact]
    public void Selecting_brave_moves_to_entry_state()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.BeginBackendSelection();
        vm.SelectBackendForEditing("brave");

        Assert.Equal(SearchConfigEditorScreen.Entry, vm.CurrentScreen.Value);
        Assert.Equal("brave", vm.CurrentBackendValue);
        Assert.Equal("Search.BraveApiKey", vm.CurrentProviderField?.Path);
    }

    [Fact]
    public void Selecting_duckduckgo_enters_zero_config_workflow_state()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.BeginBackendSelection();
        vm.SelectBackendForEditing("duckduckgo");

        Assert.Equal(SearchConfigEditorScreen.Entry, vm.CurrentScreen.Value);
        Assert.Null(vm.CurrentProviderField);
    }

    [Fact]
    public void Selecting_zero_config_provider_keeps_workflow_clean_when_effective_value_is_unchanged()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.BeginBackendSelection();
        vm.SelectBackendForEditing("brave");
        vm.BeginBackendSelection();
        vm.SelectBackendForEditing("duckduckgo");

        Assert.Equal(SearchConfigEditorScreen.Entry, vm.CurrentScreen.Value);
        Assert.False(vm.IsDirty);
        Assert.Equal("duckduckgo", vm.CurrentBackendValue);
    }

    [Fact]
    public async Task Brave_probe_failure_opens_override_dialog_before_save()
    {
        using var vm = new SearchConfigEditorViewModel(_paths, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        vm.SelectBackendForEditing("brave");
        vm.StageFieldValue("Search.BraveApiKey", "bad-key");

        await vm.SubmitCurrentConfigurationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SearchConfigEditorDialog.ProbeWarning, vm.ActiveDialog.Value);
        Assert.Equal(SearchConfigEditorScreen.Entry, vm.CurrentScreen.Value);
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
    public void Blank_secret_without_existing_value_is_still_structurally_invalid()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SetFieldValue("Search.Backend", "brave");
        vm.SetFieldValue("Search.BraveApiKey", "");

        var issues = vm.ValidationSummary.Value.IssuesFor("Search.BraveApiKey");
        Assert.Contains(issues, static issue => issue.Message.Contains("API key", StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.HasPersistedSecret("Search.BraveApiKey"));
    }

    [Fact]
    public void Switching_to_duckduckgo_preserves_inactive_searxng_endpoint()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Search": {
                "Backend": "searxng",
                "SearXngEndpoint": "https://search.example.com"
              }
            }
            """);

        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SelectBackendForEditing("duckduckgo");
        vm.SaveWithoutProbeOverride();

        var reloaded = new SearchConfigEditorViewModel(_paths);
        var config = File.ReadAllText(_paths.NetclawConfigPath);

        Assert.Contains("\"Backend\": \"duckduckgo\"", config, StringComparison.Ordinal);
        Assert.Contains("\"SearXngEndpoint\": \"https://search.example.com\"", config, StringComparison.Ordinal);
        Assert.Equal("https://search.example.com", reloaded.FieldValues["Search.SearXngEndpoint"].Value);
    }

    [Fact]
    public async Task Successful_probe_allows_save_without_dialog()
    {
        using var vm = new SearchConfigEditorViewModel(_paths, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"web\":{\"results\":[]}}", Encoding.UTF8, "application/json"),
            }));

        vm.SelectBackendForEditing("brave");
        vm.StageFieldValue("Search.BraveApiKey", "good-key");

        await vm.SubmitCurrentConfigurationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SearchConfigEditorDialog.None, vm.ActiveDialog.Value);
        Assert.Equal(SearchConfigEditorScreen.Saved, vm.CurrentScreen.Value);
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

        vm.SelectBackendForEditing("searxng");
        vm.StageFieldValue("Search.SearXngEndpoint", "https://search.example.com");
        vm.CommitFieldValue("Search.SearXngEndpoint");
        vm.OnDeactivating();
        vm.OnActivated();

        Assert.Equal("searxng", vm.FieldValues["Search.Backend"].Value);
        Assert.Equal("https://search.example.com", vm.FieldValues["Search.SearXngEndpoint"].Value);
    }

    [Fact]
    public void Invalid_endpoint_submission_keeps_typed_draft_without_mutating_accepted_value()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SelectBackendForEditing("searxng");
        var field = Assert.IsType<ProjectedConfigField>(vm.CurrentProviderField);

        var result = vm.CommitField(field.Path, "search.local");

        Assert.False(result.Success);
        Assert.Equal("search.local", vm.FieldValues[field.Path].Value);
        Assert.Equal("(not configured)", vm.GetDisplayValue(field));
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
