// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using System.Text;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Config;
using Netclaw.Cli.Tui.Sections;
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
    public void Navigate_back_resets_preserved_editor_to_provider_selection()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);
        string? route = null;
        vm.RouteRequested = value => route = value;

        vm.SaveWithoutProbeOverride();
        vm.NavigateBack();

        Assert.Equal("/config", route);
        Assert.Equal(SearchConfigEditorScreen.ProviderSelection, vm.CurrentScreen.Value);
        Assert.Equal(SearchConfigEditorDialog.None, vm.ActiveDialog.Value);
    }

    [Fact]
    public async Task Brave_probe_failure_opens_override_dialog_before_save()
    {
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var secretsExistedBefore = File.Exists(_paths.SecretsPath);
        using var vm = new SearchConfigEditorViewModel(_paths, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        vm.SelectBackendForEditing("brave");
        vm.StageFieldValue("Search.BraveApiKey", "bad-key");

        await vm.SubmitCurrentConfigurationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SearchConfigEditorDialog.ProbeWarning, vm.ActiveDialog.Value);
        Assert.Equal(SearchConfigEditorScreen.Entry, vm.CurrentScreen.Value);
        Assert.Equal(string.Empty, vm.Status.Value.Text);
        Assert.Contains("authentication failed", vm.LastProbeResult?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsExistedBefore, File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public async Task SubmitCurrentConfigurationAsync_surfaces_persistence_exception_to_awaited_caller()
    {
        using var vm = CreateBraveEditorWithSuccessfulProbe();
        ReplaceConfigFileWithDirectory();

        // The awaited path surfaces the persistence failure to the caller (unlike the from-input path,
        // which catches it into a status). The exact type depends on the OS/write mechanism — the
        // atomic write's File.Move onto a directory throws IOException, a denied open throws
        // UnauthorizedAccessException — so accept either persistence-IO exception rather than pinning
        // one, while still rejecting an unexpected exception.
        var ex = await Assert.ThrowsAnyAsync<SystemException>(
            () => vm.SubmitCurrentConfigurationAsync(TestContext.Current.CancellationToken));
        Assert.True(ex is IOException or UnauthorizedAccessException,
            $"Expected a persistence IO exception, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task Submit_from_input_surfaces_persistence_exception_as_status()
    {
        using var vm = CreateBraveEditorWithSuccessfulProbe();
        ReplaceConfigFileWithDirectory();

        await vm.SubmitCurrentConfigurationFromInputAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SearchConfigEditorScreen.Entry, vm.CurrentScreen.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("Search settings save failed", vm.Status.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigateBack_with_malformed_config_surfaces_error_without_crashing()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);
        vm.SelectBackendForEditing("brave");

        // Corrupt the config so the nav-back reload (_mapper.Load -> deserialize) throws JsonException
        // — previously unguarded in ReloadPersistedDraft.
        File.WriteAllText(_paths.NetclawConfigPath, "{ not valid json ");

        vm.NavigateBack(); // must not throw into the Termina event loop

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public async Task NavigateBack_during_validation_abandons_the_stale_probe_result()
    {
        var gate = new TaskCompletionSource();
        using var vm = new SearchConfigEditorViewModel(_paths, new GatedHttpClientFactory(gate.Task));
        vm.SelectBackendForEditing("searxng");
        vm.SetFieldValue("Search.SearXngEndpoint", "https://search.test.local");

        // Start validation; the probe blocks in the gated handler, so it is genuinely in flight.
        var validation = vm.SubmitCurrentConfigurationFromInputAsync(TestContext.Current.CancellationToken);

        // Navigate away while the probe is in flight: the owned CTS is cancelled, the probe is
        // abandoned, and its stale result must not overwrite the navigated-to screen.
        vm.NavigateBack();
        await validation;
        gate.SetResult();

        Assert.Equal(SearchConfigEditorScreen.ProviderSelection, vm.CurrentScreen.Value);
    }

    [Fact]
    public async Task Re_entrant_submit_while_a_probe_is_in_flight_is_ignored()
    {
        var gate = new TaskCompletionSource();
        using var vm = new SearchConfigEditorViewModel(_paths, new GatedHttpClientFactory(gate.Task));
        vm.SelectBackendForEditing("searxng");
        vm.SetFieldValue("Search.SearXngEndpoint", "https://search.test.local");

        // First submit starts a probe that blocks in the gated handler.
        var first = vm.SubmitCurrentConfigurationFromInputAsync(TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);

        // A second Enter while the first is still validating must NOT launch an overlapping probe + disk
        // write (two would race the same config file). The guard returns the same in-flight task.
        var second = vm.SubmitCurrentConfigurationFromInputAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, second);

        gate.SetResult();
        await first;
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
        Assert.Contains("ENC:", secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("BSA-live-key", secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("***REDACTED***", secrets, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_contribution_wraps_brave_api_key_as_sensitive_secret()
    {
        var mapper = new SearchEditorPersistenceMapper();
        var model = new SearchEditorModel { Backend = SearchBackend.Brave };
        model.Brave.ApiKeyDraft = "BSA-live-key";

        var contribution = mapper.BuildContribution(model);
        var secretAction = Assert.Single(contribution.SecretActionsOrEmpty);

        Assert.Equal("Search.BraveApiKey", secretAction.Path);
        Assert.Equal(SectionSecretActionKind.Set, secretAction.Action);
        Assert.NotNull(secretAction.Value);
        Assert.Equal("BSA-live-key", secretAction.Value.Value);
        Assert.Contains(contribution.FieldActionsOrEmpty,
            action => action.Path == "Search.Backend" && Equals(action.Value, "brave"));
    }

    [Fact]
    public void Search_contribution_preserves_blank_existing_brave_secret()
    {
        var mapper = new SearchEditorPersistenceMapper();
        var model = new SearchEditorModel { Backend = SearchBackend.Brave };
        model.Brave.HasPersistedApiKey = true;

        var contribution = mapper.BuildContribution(model);
        var secretAction = Assert.Single(contribution.SecretActionsOrEmpty);

        Assert.Equal("Search.BraveApiKey", secretAction.Path);
        Assert.Equal(SectionSecretActionKind.Preserve, secretAction.Action);
        Assert.Null(secretAction.Value);
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
    public void Switching_to_zero_config_backend_preserves_existing_brave_secret()
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
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1, \"Search\": { \"Backend\": \"brave\" } }");

        using var vm = new SearchConfigEditorViewModel(_paths);
        vm.SelectBackendForEditing("duckduckgo");
        vm.SaveWithoutProbeOverride();

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Search.BraveApiKey", out var braveKey));
        Assert.Equal("stored-secret", ConfigFileHelper.DecryptIfEncrypted(_paths, braveKey?.ToString()));
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
    public void Searxng_endpoint_requires_http_or_https_uri()
    {
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SetFieldValue("Search.Backend", "searxng");
        var result = vm.CommitField("Search.SearXngEndpoint", "ftp://search.example.com");

        Assert.False(result.Success);
        Assert.Contains(result.Issues,
            static issue => issue.Message.Contains("http:// or https://", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ftp://search.example.com", vm.FieldValues["Search.SearXngEndpoint"].Value);
    }

    [Fact]
    public void Save_anyway_blocks_structural_errors_without_persistence()
    {
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SearchConfigEditorViewModel(_paths);

        vm.SetFieldValue("Search.Backend", "brave");
        vm.SaveWithoutProbeOverride();

        Assert.Equal(SearchConfigEditorDialog.None, vm.ActiveDialog.Value);
        Assert.Equal(SearchConfigEditorScreen.Entry, vm.CurrentScreen.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("API key", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));
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

    private SearchConfigEditorViewModel CreateBraveEditorWithSuccessfulProbe()
    {
        var vm = new SearchConfigEditorViewModel(_paths, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"web\":{\"results\":[]}}", Encoding.UTF8, "application/json"),
            }));
        vm.SelectBackendForEditing("brave");
        vm.StageFieldValue("Search.BraveApiKey", "good-key");
        return vm;
    }

    private void ReplaceConfigFileWithDirectory()
    {
        File.Delete(_paths.NetclawConfigPath);
        Directory.CreateDirectory(_paths.NetclawConfigPath);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    // Blocks the probe in an awaited send until the gate completes (or the request is cancelled), so a
    // test can navigate away while the probe is genuinely in flight.
    private sealed class GatedHttpClientFactory(Task gate) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new GatedHttpMessageHandler(gate));
    }

    private sealed class GatedHttpMessageHandler(Task gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
