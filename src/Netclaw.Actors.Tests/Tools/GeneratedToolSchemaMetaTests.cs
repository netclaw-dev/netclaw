// -----------------------------------------------------------------------
// <copyright file="GeneratedToolSchemaMetaTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using System.Text.Json;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
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
}
