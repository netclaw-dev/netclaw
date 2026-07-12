// -----------------------------------------------------------------------
// <copyright file="NamedModelConfigurationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class NamedModelConfigurationTests
{
    [Fact]
    public void Resolve_LegacyShape_PreservesRuntimeValues()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Models:Main:Provider"] = "vllm",
            ["Models:Main:ModelId"] = "qwen-vl",
            ["Models:Main:ContextWindow"] = "32768",
            ["Models:Main:InputModalities"] = "Text, Image",
        });

        var result = ModelConfigurationResolver.Resolve(configuration);

        Assert.True(result.IsLegacy);
        Assert.Equal("vllm", result.Selection.Main.Provider);
        Assert.Equal(32768, result.Selection.Main.ContextWindow);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.Selection.Main.InputModalities);
    }

    [Fact]
    public void Resolve_NamedShape_ResolvesRoleWithoutMutatingDefinition()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Models:Definitions:vision:Provider"] = "vllm",
            ["Models:Definitions:vision:ModelId"] = "qwen-vl",
            ["Models:Definitions:vision:InputModalities"] = "Text, Image",
            ["Models:Roles:Main"] = "vision",
        });

        var result = ModelConfigurationResolver.Resolve(configuration);

        Assert.False(result.IsLegacy);
        Assert.Equal("qwen-vl", result.Selection.Main.ModelId);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.Selection.Main.InputModalities);
    }

    [Fact]
    public void Resolve_MixedShape_FailsLoudly()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Models:Main:Provider"] = "vllm",
            ["Models:Main:ModelId"] = "qwen-vl",
            ["Models:Definitions:vision:Provider"] = "vllm",
            ["Models:Definitions:vision:ModelId"] = "qwen-vl",
            ["Models:Roles:Main"] = "vision",
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ModelConfigurationResolver.Resolve(configuration));

        Assert.Contains("mixes legacy", exception.Message);
    }

    [Fact]
    public void Resolve_MissingDefinition_FailsLoudly()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Models:Definitions:vision:Provider"] = "vllm",
            ["Models:Definitions:vision:ModelId"] = "qwen-vl",
            ["Models:Roles:Main"] = "missing",
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => ModelConfigurationResolver.Resolve(configuration));

        Assert.Contains("unknown definition 'missing'", exception.Message);
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
