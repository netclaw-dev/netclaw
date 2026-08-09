// -----------------------------------------------------------------------
// <copyright file="GeneratedToolSchemaMetaTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using System.Text.Json;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security.Skills;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class GeneratedToolSchemaMetaTests
{
    [Fact]
    public void Generated_schema_includes_rationale_as_required_string()
    {
        var tool = new FileReadTool(new ToolConfig(), new NetclawPaths(), new Netclaw.Security.ToolPathPolicy([]));
        var schema = tool.ParameterSchema;
        var props = schema.GetProperty("properties");

        Assert.True(props.TryGetProperty("_rationale", out var rationale));
        Assert.Equal("string", rationale.GetProperty("type").GetString());

        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("_rationale", requiredNames);
    }

    [Fact]
    public void Generated_schema_includes_timeout_seconds_as_optional_integer()
    {
        var tool = new FileReadTool(new ToolConfig(), new NetclawPaths(), new Netclaw.Security.ToolPathPolicy([]));
        var schema = tool.ParameterSchema;
        var props = schema.GetProperty("properties");

        Assert.True(props.TryGetProperty("_timeout_seconds", out var timeout));
        Assert.Equal("integer", timeout.GetProperty("type").GetString());

        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("_timeout_seconds", requiredNames);
    }

    [Fact]
    public void Generated_schema_includes_background_as_optional_boolean()
    {
        var tool = new FileReadTool(new ToolConfig(), new NetclawPaths(), new Netclaw.Security.ToolPathPolicy([]));
        var schema = tool.ParameterSchema;
        var props = schema.GetProperty("properties");

        Assert.True(props.TryGetProperty("_background", out var bg));
        Assert.Equal("boolean", bg.GetProperty("type").GetString());

        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("_background", requiredNames);
    }

    [Fact]
    public void Generated_ParseArguments_ignores_meta_fields()
    {
        var tool = new FileReadTool(new ToolConfig(), new NetclawPaths(), new Netclaw.Security.ToolPathPolicy([]));
        var args = ToolInput.Create("Path", "/tmp/test.txt", "_rationale", "reading config file", "_timeout_seconds", 30, "_background", false);

        // ParseArguments should succeed and ignore meta fields
        var parsed = tool.ParseArguments(args);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void SkillLoadSchemaDescribesPromptArgumentsAsStringMap()
    {
        var tool = new SkillLoadTool(
            new SkillRegistry(),
            new NoOpSkillContentScanner(),
            new UnavailablePromptLoader());

        var arguments = tool.ParameterSchema
            .GetProperty("properties")
            .GetProperty("Arguments");

        Assert.Equal("object", arguments.GetProperty("type").GetString());
        Assert.Equal("string", arguments.GetProperty("additionalProperties").GetProperty("type").GetString());
    }

    [Fact]
    public void GeneratedDictionaryBinderSupportsAllDeclaredMapShapes()
    {
        var tool = new DictionaryShapeTool();
        var parsed = tool.ParseArguments(CreateDictionaryArguments());

        Assert.Equal("read-only", parsed.ReadOnlyMap["kind"]);
        Assert.Equal("interface", parsed.InterfaceMap["kind"]);
        Assert.Equal("concrete", parsed.ConcreteMap["kind"]);
    }

    [Theory]
    [InlineData("ReadOnlyMap")]
    [InlineData("InterfaceMap")]
    [InlineData("ConcreteMap")]
    public void GeneratedDictionaryBinderRejectsMissingRequiredMap(string missingParameter)
    {
        var tool = new DictionaryShapeTool();
        var arguments = CreateDictionaryArguments();
        arguments.Remove(missingParameter);

        var error = Assert.Throws<ArgumentException>(() => tool.ParseArguments(arguments));

        Assert.Contains(missingParameter, error.Message, StringComparison.Ordinal);
        Assert.Contains("required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> CreateDictionaryArguments()
        => new(StringComparer.Ordinal)
        {
            ["ReadOnlyMap"] = new Dictionary<string, string> { ["kind"] = "read-only" },
            ["InterfaceMap"] = new Dictionary<string, string> { ["kind"] = "interface" },
            ["ConcreteMap"] = new Dictionary<string, string> { ["kind"] = "concrete" },
        };

    private sealed class UnavailablePromptLoader : IMcpPromptSkillLoader
    {
        public ValueTask<McpPromptSkillLoadResult> LoadAsync(
            McpPromptSkillSource source,
            IReadOnlyDictionary<string, string>? arguments,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(McpPromptSkillLoadResult.Failed("Unavailable."));
    }
}

[NetclawTool("dictionary_shape_test", "Exercise each string-map parameter shape.")]
internal sealed partial class DictionaryShapeTool : NetclawTool<DictionaryShapeTool.Params>
{
    public sealed record Params(
        IReadOnlyDictionary<string, string> ReadOnlyMap,
        IDictionary<string, string> InterfaceMap,
        Dictionary<string, string> ConcreteMap);

    protected override Task<string> ExecuteAsync(
        Params args,
        ToolInvocationContext context,
        CancellationToken ct)
        => Task.FromResult(string.Empty);
}
