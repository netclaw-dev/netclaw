// -----------------------------------------------------------------------
// <copyright file="ModelReferenceBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Regression coverage for the schema/binding mismatch tracked in issue #988.
/// <see cref="ModelReference.InputModalities"/> and
/// <see cref="ModelReference.OutputModalities"/> are scalar nullable flag
/// enums; the schema is now aligned to a comma-separated string. Verifies
/// that .NET configuration binding correctly OR-combines flags from the
/// string form so the operator's manual override actually reaches
/// <c>ModelCapabilityResolution</c>.
/// </summary>
public sealed class ModelReferenceBindingTests
{
    [Fact]
    public void CommaSeparatedString_BindsToCombinedFlags()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "my-vllm",
                ["Models:Main:ModelId"] = "qwen36-ultimate",
                ["Models:Main:InputModalities"] = "Text, Image",
                ["Models:Main:OutputModalities"] = "Text",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        var main = selection.Main;

        Assert.Equal("qwen36-ultimate", main.ModelId);
        Assert.Equal(ModelModality.Text | ModelModality.Image, main.InputModalities);
        Assert.Equal(ModelModality.Text, main.OutputModalities);
    }

    [Fact]
    public void SingleValueString_BindsToSingleFlag()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "p",
                ["Models:Main:ModelId"] = "m",
                ["Models:Main:InputModalities"] = "Text",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        Assert.Equal(ModelModality.Text, selection.Main.InputModalities);
    }

    [Fact]
    public void AbsentField_LeavesNullForDownstreamDefaulting()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "p",
                ["Models:Main:ModelId"] = "m",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        // Null preserves the "no manual override" signal so
        // ModelCapabilityResolution can fall back to detected/default.
        Assert.Null(selection.Main.InputModalities);
        Assert.Null(selection.Main.OutputModalities);
    }

    [Fact]
    public void AllFlags_RoundTrip()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "p",
                ["Models:Main:ModelId"] = "m",
                ["Models:Main:InputModalities"] = "Text, Image, Audio, Video",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        Assert.Equal(
            ModelModality.Text | ModelModality.Image | ModelModality.Audio | ModelModality.Video,
            selection.Main.InputModalities);
    }

    [Fact]
    public void limit_and_thinking_bind_from_named_definition()
    {
        var main = ModelConfigurationResolver.Resolve(NamedDefinition("MaxOutputTokens", "32000", "Thinking", "Low"))
            .Selection.Main;

        Assert.Equal(32000, main.MaxOutputTokens);
        Assert.Equal(ThinkingLevel.Low, main.Thinking);
    }

    [Theory]
    [InlineData("invalid_thinking_fails_with_key_path", "Thinking", "Turbo")]
    [InlineData("zero_limit_fails_with_key_path", "MaxOutputTokens", "0")]
    public void Invalid_setting_fails_with_key_path(string row, string key, string value)
    {
        var check = ModelConfigurationValidation.Check(NamedDefinition(key, value));

        Assert.True(check.Error is not null, row);
        Assert.Contains($"Models:Definitions:d:{key}", check.Error, StringComparison.Ordinal);
        Assert.Null(check.Valid);
    }

    private static IConfiguration NamedDefinition(params string[] keyValues)
    {
        var values = new Dictionary<string, string?>
        {
            ["Providers:p:Type"] = "ollama",
            ["Models:Definitions:d:Provider"] = "p",
            ["Models:Definitions:d:ModelId"] = "m",
            ["Models:Roles:Main"] = "d",
        };
        for (var i = 0; i < keyValues.Length; i += 2)
            values[$"Models:Definitions:d:{keyValues[i]}"] = keyValues[i + 1];
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
