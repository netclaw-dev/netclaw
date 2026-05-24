// -----------------------------------------------------------------------
// <copyright file="ProviderConfigurationLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration.Providers;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ProviderConfigurationLoaderTests
{
    [Fact]
    public void Load_PreservesVendorOptionsBag()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:ollama:Type"] = "ollama",
                ["Providers:ollama:VendorOptions:DisableThinking"] = "true",
                ["Providers:openrouter:Type"] = "openrouter",
                ["Providers:openrouter:VendorOptions:Nested:Mode"] = "auto",
                ["Providers:openrouter:VendorOptions:Nested:Retries"] = "2",
                ["Providers:openrouter:VendorOptions:Tags:0"] = "search",
                ["Providers:openrouter:VendorOptions:Tags:1"] = "chat"
            })
            .Build();

        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        Assert.True(providers["ollama"].VendorOptions?["DisableThinking"]?.GetValue<bool>());
        Assert.Equal("auto", providers["openrouter"].VendorOptions?["Nested"]?["Mode"]?.GetValue<string>());
        Assert.Equal(2L, providers["openrouter"].VendorOptions?["Nested"]?["Retries"]?.GetValue<long>());
        Assert.Equal("search", providers["openrouter"].VendorOptions?["Tags"]?[0]?.GetValue<string>());
        Assert.Equal("chat", providers["openrouter"].VendorOptions?["Tags"]?[1]?.GetValue<string>());
    }

    [Fact]
    public void GetVendorOptions_DeserializesTypedOptions()
    {
        var entry = new ProviderEntry
        {
            Type = "ollama",
            VendorOptions = new System.Text.Json.Nodes.JsonObject
            {
                ["DisableThinking"] = true,
                ["Mode"] = "fast"
            }
        };

        var options = entry.GetVendorOptions<TestVendorOptions>();

        Assert.NotNull(options);
        Assert.True(options!.DisableThinking);
        Assert.Equal("fast", options.Mode);
    }

    private sealed class TestVendorOptions : IVendorOptions
    {
        public bool DisableThinking { get; set; }
        public string? Mode { get; set; }
    }
}
