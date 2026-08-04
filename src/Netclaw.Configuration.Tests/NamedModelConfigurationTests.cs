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
        Assert.Equal("qwen-vl", result.Runtime.Definitions[result.Runtime.Roles.Main].ModelId);
        Assert.Null(result.Runtime.Proxies.Image);
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
        Assert.Equal("vision", result.Runtime.Roles.Main);
        Assert.Equal("qwen-vl", result.Runtime.Definitions["VISION"].ModelId);
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

        var exception = Assert.Throws<ModelConfigurationException>(
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

        var exception = Assert.Throws<ModelConfigurationException>(
            () => ModelConfigurationResolver.Resolve(configuration));

        Assert.Contains("unknown definition 'missing'", exception.Message);
    }

    [Fact]
    public void Resolve_ImageProxy_RetainsNamedAssignment()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Models:Definitions:main:Provider"] = "vllm",
            ["Models:Definitions:main:ModelId"] = "qwen-text",
            ["Models:Definitions:vision:Provider"] = "vllm",
            ["Models:Definitions:vision:ModelId"] = "qwen-vl",
            ["Models:Roles:Main"] = "main",
            ["Models:Proxies:Image"] = "vision",
        });

        var result = ModelConfigurationResolver.Resolve(configuration);

        Assert.Equal("vision", result.Runtime.Proxies.Image);
        Assert.Equal("qwen-vl", result.Runtime.Definitions["vision"].ModelId);
    }

    [Fact]
    public void Resolve_UnknownImageProxy_FailsLoudly()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Models:Definitions:main:Provider"] = "vllm",
            ["Models:Definitions:main:ModelId"] = "qwen-text",
            ["Models:Roles:Main"] = "main",
            ["Models:Proxies:Image"] = "missing",
        });

        var exception = Assert.Throws<ModelConfigurationException>(
            () => ModelConfigurationResolver.Resolve(configuration));

        Assert.Contains("Models:Proxies:Image", exception.Message);
        Assert.Contains("unknown definition 'missing'", exception.Message);
    }

    [Fact]
    public void Resolve_ProxyWithLegacyRoles_FailsLoudly()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Models:Main:Provider"] = "vllm",
            ["Models:Main:ModelId"] = "qwen-text",
            ["Models:Proxies:Image"] = "vision",
        });

        var exception = Assert.Throws<ModelConfigurationException>(
            () => ModelConfigurationResolver.Resolve(configuration));

        Assert.Contains("mixes legacy", exception.Message);
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
