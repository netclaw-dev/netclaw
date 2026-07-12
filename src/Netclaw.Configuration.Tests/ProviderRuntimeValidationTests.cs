// -----------------------------------------------------------------------
// <copyright file="ProviderRuntimeValidationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ProviderRuntimeValidationTests
{
    [Theory]
    [InlineData("vision", true, true, true)]
    [InlineData("VISION", true, true, true)]
    [InlineData("missing", true, false, false)]
    [InlineData("provider-only", true, true, false)]
    [InlineData(null, false, false, false)]
    public void NamedRolePresence_ComesFromReferencedDefinition(
        string? roleReference,
        bool roleConfigured,
        bool providerConfigured,
        bool modelIdConfigured)
    {
        var models = JsonNode.Parse(
            """
            {
              "Definitions": {
                "vision": { "Provider": "vllm", "ModelId": "qwen-vl" },
                "provider-only": { "Provider": "vllm" }
              },
              "Roles": {}
            }
            """)!.AsObject();
        if (roleReference is not null)
            models["Roles"]!["Main"] = roleReference;

        var root = new JsonObject { ["Models"] = models };
        var result = ProviderRuntimeConfiguration.FromJson(root).Main;

        Assert.Equal(roleConfigured, result.RoleConfigured);
        Assert.Equal(providerConfigured, result.ProviderConfigured);
        Assert.Equal(modelIdConfigured, result.ModelIdConfigured);
    }

    [Fact]
    public void MainModelAbsent_DefaultModelSelection_ReturnsNoProviderConfigured()
    {
        // Bound defaults are not configuration. A fresh install with no Models:Main
        // section must be treated as explicitly unconfigured.
        var validation = ProviderRuntimeValidation.Evaluate(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection(),
            ProviderRuntimeConfiguration.FromExplicitRoles(
                new Dictionary<string, ProviderEntry>(), main: false, fallback: false, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Empty(validation.AvailableProviders);
        Assert.Contains("Models:Main missing", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderExistsButMainModelAbsent_DoesNotTreatDefaultsAsConfigured()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["local-ollama"] = new ProviderEntry { Type = "ollama" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection(),
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: false, fallback: false, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Contains("Models:Main missing", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderExistsButMainSectionHasNoProviderOrModel_DoesNotTreatDefaultsAsConfigured()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["local-ollama"] = new ProviderEntry { Type = "ollama" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection(),
            new ProviderRuntimeConfiguration(
                Main: new ModelReferenceRuntimeConfiguration(
                    RoleConfigured: true,
                    ProviderConfigured: false,
                    ModelIdConfigured: false),
                Fallback: ModelReferenceRuntimeConfiguration.FromCompleteRole(false),
                Compaction: ModelReferenceRuntimeConfiguration.FromCompleteRole(false),
                ProvidersWithExplicitType: ["local-ollama"]));

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Contains("no model selected", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyProviders_EmptyModel_ReturnsNoProviderConfigured_WithBothEmptyMessage()
    {
        var validation = ProviderRuntimeValidation.Evaluate(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection { Main = new ModelReference { Provider = "", ModelId = "" } },
            ProviderRuntimeConfiguration.FromExplicitRoles(
                new Dictionary<string, ProviderEntry>(), main: true, fallback: false, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Contains("no providers or models", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProvidersConfigured_ModelMissing_ReturnsNoProviderConfigured_WithProvidersListed()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = new ProviderEntry { Type = "openrouter" },
            ["ollama"] = new ProviderEntry { Type = "ollama" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection { Main = new ModelReference { Provider = "", ModelId = "" } },
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: true, fallback: false, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Equal(2, validation.AvailableProviders.Count);
        Assert.Contains("openrouter", validation.AvailableProviders);
    }

    [Fact]
    public void ProvidersConfigured_ModelPointsToUnknownProvider_ReturnsNoProviderConfigured()
    {
        // Regression: operator typo in Models:Main.Provider (e.g. "ollama-local1"
        // vs configured "ollama-local") used to crash the daemon with a raw
        // ProviderPluginFactory stack trace. Same operator remediation as
        // genuinely-no-provider, so select No-Op and surface available providers
        // in the banner.
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["github-copilot"] = new ProviderEntry { Type = "github-copilot" },
            ["my-openrouter"] = new ProviderEntry { Type = "openrouter" },
            ["ollama-local"] = new ProviderEntry { Type = "ollama" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection { Main = new ModelReference { Provider = "ollama-local1", ModelId = "qwen3:30b" } },
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: true, fallback: false, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Contains("ollama-local1", validation.Reason);
        Assert.Contains("ollama-local", validation.Reason);
        Assert.Equal(3, validation.AvailableProviders.Count);
    }

    [Fact]
    public void ValidProviderAndModel_ReturnsValid()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = new ProviderEntry { Type = "openrouter" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection { Main = new ModelReference { Provider = "openrouter", ModelId = "anthropic/claude-haiku-4" } },
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: true, fallback: false, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.Valid, validation.Status);
        Assert.Null(validation.Reason);
        Assert.Single(validation.AvailableProviders);
    }

    [Fact]
    public void ExplicitFallbackWithUnknownProvider_ReturnsInvalid()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = new ProviderEntry { Type = "openrouter" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection
            {
                Main = new ModelReference { Provider = "openrouter", ModelId = "anthropic/claude-haiku-4" },
                Fallback = new ModelReference { Provider = "missing", ModelId = "qwen3:30b" },
            },
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: true, fallback: true, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.Invalid, validation.Status);
        Assert.Contains("Fallback", validation.Reason);
        Assert.Contains("missing", validation.Reason);
    }

    [Fact]
    public void ExplicitCompactionWithIncompleteModel_ReturnsInvalid()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = new ProviderEntry { Type = "openrouter" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection
            {
                Main = new ModelReference { Provider = "openrouter", ModelId = "anthropic/claude-haiku-4" },
                Compaction = new ModelReference { Provider = "openrouter", ModelId = "" },
            },
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: true, fallback: false, compaction: true));

        Assert.Equal(ProviderRuntimeStatus.Invalid, validation.Status);
        Assert.Contains("Compaction", validation.Reason);
        Assert.Contains("incomplete", validation.Reason);
    }

    [Fact]
    public void ReferencedProviderWithoutExplicitType_ReturnsInvalid()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = new ProviderEntry { Type = "openrouter" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection { Main = new ModelReference { Provider = "openrouter", ModelId = "anthropic/claude-haiku-4" } },
            new ProviderRuntimeConfiguration(
                Main: ModelReferenceRuntimeConfiguration.FromCompleteRole(true),
                Fallback: ModelReferenceRuntimeConfiguration.FromCompleteRole(false),
                Compaction: ModelReferenceRuntimeConfiguration.FromCompleteRole(false),
                ProvidersWithExplicitType: []));

        Assert.Equal(ProviderRuntimeStatus.Invalid, validation.Status);
        Assert.Contains("missing required Type", validation.Reason);
    }
}
