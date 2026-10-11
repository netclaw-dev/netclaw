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

    public static TheoryData<string, string[], string[]> BrokenShapes() => new()
    {
        // Mixed: both sets of keys are named, and the message says which set to remove.
        {
            """{"Main":{"Provider":"p","ModelId":"m1"},"Fallback":{"Provider":"p","ModelId":"m2"},"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"d"}}""",
            ["mixes legacy inline roles (Models:Main, Models:Fallback)", "current keys (Models:Definitions, Models:Roles)",
             "remove Models:Main, Models:Fallback", "remove Models:Definitions, Models:Roles", "NETCLAW_Models__*"],
            ["doctor --fix"]
        },
        {
            """{"Main":{"Provider":"p","ModelId":"m1"},"Roles":{"Main":"d"}}""",
            ["mixes legacy inline roles (Models:Main)", "current keys (Models:Roles)"],
            ["doctor --fix"]
        },
        {
            """{"Definitions":{"d1":{"Provider":"p","ModelId":"m1"},"d2":{"Provider":"p","ModelId":"m2"}}}""",
            ["Models:Definitions is set but Models:Roles is missing or empty", "\"Main\"", "d1, d2", "If Models:Roles is an empty {} in netclaw.json, `netclaw model set main <provider> <model>` repairs it"],
            ["doctor --fix"]
        },
        {
            """{"Roles":{"Main":"d"}}""",
            ["Models:Roles is set but Models:Definitions is missing or empty", "If Models:Definitions is an empty {} in netclaw.json, `netclaw model set main <provider> <model>` repairs it", "If the key is absent, remove Models:Roles first"],
            ["doctor --fix"]
        },
        {
            """{"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Fallback":"d"}}""",
            ["Models:Roles:Main must name a model definition", "`netclaw model set main <provider> <model>`"],
            ["doctor --fix"]
        },
        {
            """{"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"nope"}}""",
            ["Models:Roles:Main references unknown definition 'nope'", "Defined: d", "`netclaw model set main <provider> <model>`"],
            ["doctor --fix"]
        },
        {
            """{"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"d","Compaction":"nope"}}""",
            ["Models:Roles:Compaction references unknown definition 'nope'", "`netclaw model set compaction <provider> <model>`"],
            ["doctor --fix"]
        },
        // The binder rejects an unknown modality with a plain InvalidOperationException.
        {
            """{"Main":{"Provider":"p","ModelId":"m1","InputModalities":"banana"}}""",
            ["Models:Main:InputModalities", "banana"],
            ["doctor --fix"]
        },
        // The binder silently drops a definition it cannot read. The message names the definition and key.
        {
            """{"Definitions":{"d":{"Provider":"p","ModelId":"m1","InputModalities":"banana"}},"Roles":{"Main":"d"}}""",
            ["Models:Definitions:d:InputModalities", "banana"],
            ["doctor --fix", "unknown definition"]
        },
        {
            """{"Definitions":{"d":{"Provider":"p","ModelId":"m1"},"unused":{"Provider":"p","ModelId":"m2","ContextWindow":"abc"}},"Roles":{"Main":"d"}}""",
            ["Models:Definitions:unused:ContextWindow", "abc"],
            ["doctor --fix", "unknown definition"]
        },
    };

    [Theory]
    [MemberData(nameof(BrokenShapes))]
    public void Resolve_BrokenShape_NamesTheKeysAndTheRepair(string modelsJson, string[] expected, string[] forbidden)
    {
        var configuration = BuildJson(modelsJson);

        var exception = Assert.Throws<ModelConfigurationException>(
            () => ModelConfigurationResolver.Resolve(configuration));
        Assert.False(ModelConfigurationResolver.TryResolve(configuration, out var resolution, out var error));

        Assert.Null(resolution);
        Assert.Equal(exception.Message, error);
        foreach (var fragment in expected)
            Assert.Contains(fragment, error);
        foreach (var fragment in forbidden)
            Assert.DoesNotContain(fragment, error);
    }

    [Fact]
    public void Resolve_MixedShape_NamesTheEnvironmentVariablesThatAreSet()
    {
        const string variable = "NETCLAW_Models__Main__ModelId";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            var configuration = BuildJson("""{"Main":{"Provider":"p","ModelId":"m1"},"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"d"}}""");

            Assert.False(ModelConfigurationResolver.TryResolve(configuration, out _, out var error));
            Assert.Contains($"(set now: {variable})", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void TryResolve_ValidShape_ReturnsResolution()
    {
        var configuration = BuildJson("""{"Definitions":{"d":{"Provider":"p","ModelId":"m1"}},"Roles":{"Main":"d"}}""");

        Assert.True(ModelConfigurationResolver.TryResolve(configuration, out var resolution, out var error));

        Assert.Null(error);
        Assert.Equal("m1", resolution.Selection.Main.ModelId);
    }

    private static IConfiguration BuildJson(string modelsJson)
        => new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes($$"""{"Models":{{modelsJson}}}""")))
            .Build();

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
