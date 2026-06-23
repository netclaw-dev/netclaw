// -----------------------------------------------------------------------
// <copyright file="SearchSectionSpecTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Cli.Tui.Config;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SearchSectionSpecTests
{
    [Fact]
    public void Fields_are_projected_from_runtime_config_metadata_keys()
    {
        var spec = new SearchSectionSpec();

        Assert.Contains(spec.Fields, field => field.Path == "Search.Backend");

        var brave = Assert.Single(spec.Fields, field => field.Path == "Search.BraveApiKey");
        Assert.Equal(ConfigFieldStorage.SecretsFile, brave.Storage);
        Assert.Equal(ConfigFieldWidget.PasswordInput, brave.Widget);
        Assert.True(brave.PreserveBlankSecret);

        var searXng = Assert.Single(spec.Fields, field => field.Path == "Search.SearXngEndpoint");
        Assert.Equal(ConfigFieldStorage.ConfigFile, searXng.Storage);
        Assert.Equal(ConfigFieldWidget.TextInput, searXng.Widget);
    }

    [Fact]
    public void Provider_field_follows_selected_backend()
    {
        var spec = new SearchSectionSpec();
        var model = new SearchEditorModel { Backend = SearchBackend.Brave };

        Assert.Equal("Search.BraveApiKey", spec.GetProviderField(model)?.Path);

        model.Backend = SearchBackend.SearXng;
        Assert.Equal("Search.SearXngEndpoint", spec.GetProviderField(model)?.Path);

        model.Backend = SearchBackend.DuckDuckGo;
        Assert.Null(spec.GetProviderField(model));
    }
}
