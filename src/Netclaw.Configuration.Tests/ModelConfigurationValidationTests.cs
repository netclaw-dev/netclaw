// -----------------------------------------------------------------------
// <copyright file="ModelConfigurationValidationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ModelConfigurationValidationTests
{
    private const string Providers = """{"p":{"Type":"ollama","Endpoint":"http://127.0.0.1:9","AuthMethod":"None"}}""";

    [Theory]
    [InlineData("""{"Main":{"Provider":"p","ModelId":"m1"}}""")]
    [InlineData("""{"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"d"}}""")]
    public void ValidShapes_HaveNoError(string modelsJson)
    {
        var check = ModelConfigurationValidation.Check(Build(modelsJson));

        Assert.Null(check.Error);
        Assert.Equal(ProviderRuntimeStatus.Valid, check.Valid!.Validation.Status);
        Assert.Equal("m1", check.Valid!.Models.Main.ModelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"Main":{"Provider":"typo","ModelId":"m1"}}""")]
    public void NoMainModel_IsTheNoOpOutcome_NotAnError(string? modelsJson)
    {
        var check = ModelConfigurationValidation.Check(Build(modelsJson));

        Assert.Null(check.Error);
        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, check.Valid!.Validation.Status);
    }

    [Theory]
    [InlineData("""{"Main":{"Provider":"p","ModelId":"m1"},"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"d"}}""", "mixes legacy inline roles")]
    [InlineData("""{"Definitions":{"d":{"Provider":"p","ModelId":"m1"}}}""", "Models:Roles is missing or empty")]
    [InlineData("""{"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"nope"}}""", "unknown definition 'nope'")]
    [InlineData("""{"Definitions":{"d":{"Provider":"p","ModelId":"m1","InputModalities":"banana"}},"Roles":{"Main":"d"}}""", "Models:Definitions:d:InputModalities")]
    // The role-bound ContextWindow check names the definition the operator edits.
    [InlineData("""{"Definitions":{"d":{"Provider":"p","ModelId":"m1","ContextWindow":100}},"Roles":{"Main":"d"}}""", "Models:Definitions:d:ContextWindow (100) is below minimum")]
    [InlineData("""{"Main":{"Provider":"p","ModelId":"m1","ContextWindow":100}}""", "Models:Main:ContextWindow (100) is below minimum")]
    // A role whose definition names a provider that is not configured.
    [InlineData("""{"Definitions":{"d":{"Provider":"p","ModelId":"m1"},"f":{"Provider":"zzz","ModelId":"m2"}},"Roles":{"Main":"d","Fallback":"f"}}""", "provider 'zzz' which is not configured")]
    [InlineData("""{"Main":{"Provider":"p","ModelId":"m1"},"Compaction":{"Provider":"zzz","ModelId":"m2"}}""", "provider 'zzz' which is not configured")]
    public void InvalidShapes_HaveOneError(string modelsJson, string expected)
    {
        var check = ModelConfigurationValidation.Check(Build(modelsJson));

        Assert.NotNull(check.Error);
        Assert.Contains(expected, check.Error);
        Assert.DoesNotContain("doctor --fix", check.Error);

        // Startup calls Require: the same error leaves as the exception that Program.cs turns into a clean exit.
        var exception = Assert.Throws<ModelConfigurationException>(() => ModelConfigurationValidation.Require(Build(modelsJson)));
        Assert.Equal(check.Error, exception.Message);
    }

    [Fact]
    public void Require_ReturnsTheValidConfiguration()
    {
        var valid = ModelConfigurationValidation.Require(Build("""{"Main":{"Provider":"p","ModelId":"m1"}}"""));

        Assert.Equal("m1", valid.Models.Main.ModelId);
    }

    // The binder or the VendorOptions check rejects these. The error names the provider.
    [Theory]
    [InlineData("""{"Type":"ollama","AuthMethod":"banana"}""")]
    [InlineData("""{"Type":"ollama","VendorOptions":"x"}""")]
    [InlineData("""{"Type":"ollama","OAuthTokenExpiry":"garbage"}""")]
    public void ProviderThatCannotBeLoaded_IsAnErrorNamingTheProvider(string provider)
    {
        var check = ModelConfigurationValidation.Check(new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "{\"Providers\":{\"p\":" + provider + "},\"Models\":{\"Main\":{\"Provider\":\"p\",\"ModelId\":\"m1\"}}}")))
            .Build());

        Assert.NotNull(check.Error);
        Assert.Contains("Providers:p is invalid", check.Error);
        Assert.DoesNotContain("   at ", check.Error);
    }

    private static IConfiguration Build(string? modelsJson)
        => new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                modelsJson is null
                    ? $$"""{"Providers":{{Providers}}}"""
                    : $$"""{"Providers":{{Providers}},"Models":{{modelsJson}}}""")))
            .Build();
}
