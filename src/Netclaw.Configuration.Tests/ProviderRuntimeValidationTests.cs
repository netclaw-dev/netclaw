// -----------------------------------------------------------------------
// <copyright file="ProviderRuntimeValidationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ProviderRuntimeValidationTests
{
    [Fact]
    public void EmptyProviders_DefaultModelSelection_ReturnsNoProviderConfigured()
    {
        // Default ModelSelection.Main has a non-empty default Provider ("local-ollama")
        // and ModelId ("qwen3:30b"), so the "no providers" branch is what trips here.
        var validation = ProviderRuntimeValidation.Evaluate(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection());

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Empty(validation.AvailableProviders);
        Assert.Contains("no providers", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyProviders_EmptyModel_ReturnsNoProviderConfigured_WithBothEmptyMessage()
    {
        var validation = ProviderRuntimeValidation.Evaluate(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection { Main = new ModelReference { Provider = "", ModelId = "" } });

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
            new ModelSelection { Main = new ModelReference { Provider = "", ModelId = "" } });

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);
        Assert.Equal(2, validation.AvailableProviders.Count);
        Assert.Contains("openrouter", validation.AvailableProviders);
    }

    [Fact]
    public void ProvidersConfigured_ModelPointsToUnknownProvider_ReturnsInvalid()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = new ProviderEntry { Type = "openrouter" },
        };

        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            new ModelSelection { Main = new ModelReference { Provider = "anthropic", ModelId = "claude-4" } });

        Assert.Equal(ProviderRuntimeStatus.Invalid, validation.Status);
        Assert.Contains("unknown provider", validation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anthropic", validation.Reason);
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
            new ModelSelection { Main = new ModelReference { Provider = "openrouter", ModelId = "anthropic/claude-haiku-4" } });

        Assert.Equal(ProviderRuntimeStatus.Valid, validation.Status);
        Assert.Null(validation.Reason);
        Assert.Single(validation.AvailableProviders);
    }
}
