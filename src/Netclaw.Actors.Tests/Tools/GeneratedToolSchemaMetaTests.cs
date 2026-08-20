// -----------------------------------------------------------------------
// <copyright file="GeneratedToolSchemaMetaTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using System.Text.Json;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Reminders;
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
    public void Generated_schema_describes_string_arrays_without_scalar_coercion()
    {
        var tool = new FileReadManyTool(
            new ToolConfig(),
            new NetclawPaths(),
            new Netclaw.Security.ToolPathPolicy([]));

        var paths = tool.ParameterSchema
            .GetProperty("properties")
            .GetProperty("Paths");

        Assert.Equal("array", paths.GetProperty("type").GetString());
        Assert.Equal("string", paths.GetProperty("items").GetProperty("type").GetString());
        Assert.Contains(
            "Relative paths use the current project",
            paths.GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Paths",
            tool.ParameterSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
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

    [Fact]
    public void Reminder_schema_exposes_closed_delivery_variants()
    {
        var schema = CreateReminderTool().ParameterSchema;

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var variants = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(3, variants.Length);
        Assert.Equal(
            ["current_session", "channel", "none"],
            variants.Select(static variant => variant
                .GetProperty("properties")
                .GetProperty("DeliveryKind")
                .GetProperty("enum")[0]
                .GetString()!));

        var channelRequired = variants[1]
            .GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        Assert.Equal(["DeliveryKind", "DeliveryTransport", "DeliveryAddress"], channelRequired);
    }

    [Fact]
    public Task Conditional_tool_schemas_match_snapshot()
    {
        var document = JsonSerializer.Serialize(new
        {
            Reminder = CreateReminderTool().ParameterSchema,
            SkillManage = CreateSkillManageTool().ParameterSchema
        });
        return Verifier.VerifyJson(document);
    }

    [Fact]
    public void Single_shape_schema_does_not_gain_conditional_keywords()
    {
        var schema = new FileListTool(
            new ToolConfig(),
            new NetclawPaths(),
            new Netclaw.Security.ToolPathPolicy([])).ParameterSchema;

        Assert.False(schema.TryGetProperty("oneOf", out _));
        Assert.False(schema.TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public void Conditional_schema_and_spill_resolver_seams_are_not_public_api()
    {
        var attributeType = typeof(NetclawToolAttribute).Assembly.GetType(
            "Netclaw.Tools.ToolArgumentVariantAttribute",
            throwOnError: true)!;

        Assert.False(attributeType.IsPublic);
        Assert.False(typeof(ToolOutputReadTool).IsPublic);
        Assert.False(typeof(ToolOutputSpillLocation).IsPublic);
    }

    [Theory]
    [MemberData(nameof(InvalidReminderArguments))]
    public void Reminder_binder_rejects_invalid_delivery_branches(Dictionary<string, object?> arguments)
    {
        var error = Assert.Throws<ArgumentException>(() => CreateReminderTool().ParseArguments(arguments));

        Assert.Contains("exactly one declared variant", error.Message, StringComparison.Ordinal);
        Assert.Contains("NOT executed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reminder_binder_accepts_complete_channel_branch()
    {
        var parsed = CreateReminderTool().ParseArguments(CreateReminderArguments(
            "channel",
            deliveryTransport: "slack",
            deliveryAddress: "channel-token"));

        Assert.Equal("channel", parsed.DeliveryKind);
        Assert.Equal("slack", parsed.DeliveryTransport);
        Assert.Equal("channel-token", parsed.DeliveryAddress);
    }

    [Fact]
    public void Reminder_binder_treats_internal_null_optional_fields_as_omitted()
    {
        var arguments = CreateReminderArguments("none");
        arguments["DeliveryTransport"] = null;
        arguments["DeliveryAddress"] = JsonDocument.Parse("null").RootElement.Clone();

        var parsed = CreateReminderTool().ParseArguments(arguments);

        Assert.Equal("none", parsed.DeliveryKind);
        Assert.Null(parsed.DeliveryTransport);
        Assert.True(string.IsNullOrEmpty(parsed.DeliveryAddress));
    }

    [Fact]
    public void Skill_manage_schema_exposes_each_action_as_one_branch()
    {
        var tool = CreateSkillManageTool();
        var values = tool.ParameterSchema
            .GetProperty("oneOf")
            .EnumerateArray()
            .Select(static variant => variant
                .GetProperty("properties")
                .GetProperty("Action")
                .GetProperty("enum")[0]
                .GetString()!)
            .ToArray();

        Assert.Equal(["create", "edit", "patch", "delete", "write_file", "remove_file"], values);
    }

    public static TheoryData<Dictionary<string, object?>> InvalidReminderArguments => new()
    {
        CreateReminderArguments(null),
        CreateReminderArguments("unsupported"),
        CreateReminderArguments("channel", deliveryTransport: "slack"),
        CreateReminderArguments("current_session", deliveryTransport: "slack"),
        CreateReminderArguments("none", deliveryAddress: "channel-token"),
        WithConflictingDiscriminator(CreateReminderArguments(
            "channel",
            deliveryTransport: "slack",
            deliveryAddress: "channel-token"))
    };

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

    private static SetReminderTool CreateReminderTool()
        => new(null!, TimeProvider.System, new SchedulingConfig());

    private static SkillManageTool CreateSkillManageTool()
        => new(
            new SkillRegistry(),
            new NetclawPaths(),
            new NoOpSkillContentScanner(),
            null!);

    private static Dictionary<string, object?> CreateReminderArguments(
        string? deliveryKind,
        string? deliveryTransport = null,
        string? deliveryAddress = null)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = "test-reminder",
            ["Name"] = "Test reminder",
            ["Prompt"] = "Check status.",
            ["ScheduleType"] = "once",
            ["Schedule"] = "15m"
        };
        if (deliveryKind is not null)
            arguments["DeliveryKind"] = deliveryKind;
        if (deliveryTransport is not null)
            arguments["DeliveryTransport"] = deliveryTransport;
        if (deliveryAddress is not null)
            arguments["DeliveryAddress"] = deliveryAddress;
        return arguments;
    }

    private static Dictionary<string, object?> WithConflictingDiscriminator(
        Dictionary<string, object?> arguments)
    {
        arguments["delivery_kind"] = "none";
        return arguments;
    }

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
