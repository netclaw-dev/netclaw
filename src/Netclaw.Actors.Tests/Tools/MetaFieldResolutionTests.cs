// -----------------------------------------------------------------------
// <copyright file="MetaFieldResolutionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Spelling-tolerant resolution of per-call meta fields. ChatGPT-trained models
/// (Qwen) drop the leading underscore, capitalize, or shorten the meta names;
/// these must resolve onto the canonical fields so the value is consumed rather
/// than rejected — while genuinely unknown keys still resolve to null and are
/// rejected upstream. The collision guard proves the tool-agnostic resolution is
/// safe: no first-party tool declares a parameter that would be hijacked.
/// </summary>
public class MetaFieldResolutionTests
{
    [Theory]
    [InlineData("_rationale", "_rationale")]
    [InlineData("Rationale", "_rationale")]
    [InlineData("rationale", "_rationale")]
    [InlineData("_timeout_seconds", "_timeout_seconds")]
    [InlineData("TimeoutSeconds", "_timeout_seconds")]
    [InlineData("timeout_seconds", "_timeout_seconds")]
    [InlineData("Timeout_seconds", "_timeout_seconds")]
    [InlineData("Timeout", "_timeout_seconds")]
    [InlineData("timeout", "_timeout_seconds")]
    [InlineData("_background", "_background")]
    [InlineData("Background", "_background")]
    [InlineData("background", "_background")]
    public void ResolveMetaField_recognizes_canonical_and_chatgpt_variants(string key, string expected)
        => Assert.Equal(expected, ToolArgumentHelper.ResolveMetaField(key));

    [Theory]
    [InlineData("Command")]
    [InlineData("Task")]     // load_tool: model invented this — genuinely unknown
    [InlineData("Context")]  // load_tool: ditto
    [InlineData("Cancel")]   // set_reminder: ditto
    [InlineData("reason")]   // deliberately NOT aliased — only the observed names map
    [InlineData("Url")]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveMetaField_returns_null_for_non_meta_keys(string key)
        => Assert.Null(ToolArgumentHelper.ResolveMetaField(key));

    [Fact]
    public void ResolveMetaField_returns_null_for_null_key()
        => Assert.Null(ToolArgumentHelper.ResolveMetaField(null));

    // Tool-agnostic resolution is only safe if no real (non-meta) parameter
    // canonicalizes onto a meta field. If a future tool declares e.g. a
    // "Background" or "Timeout" parameter, this fails and forces a deliberate
    // decision rather than a silent hijack.
    [Fact]
    public void No_first_party_tool_parameter_collides_with_a_meta_field()
    {
        var config = new ToolConfig();
        var pathPolicy = new Netclaw.Security.ToolPathPolicy([]);
        var commandPolicy = new Netclaw.Security.ShellCommandPolicy();
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            pathPolicy,
            commandPolicy,
            toolAccessPolicy: TestToolAccessPolicy.Create(config, commandPolicy, pathPolicy));

        var collisions = new List<string>();
        foreach (var registration in registry.GetAllRegistrations())
        {
            var schema = registration.Tool.ParameterSchema;
            if (!schema.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var prop in props.EnumerateObject())
            {
                // Meta fields are injected into every schema and legitimately
                // resolve; only declared (non-_) parameters must stay clear.
                if (prop.Name.StartsWith('_'))
                    continue;

                if (ToolArgumentHelper.ResolveMetaField(prop.Name) is { } canonical)
                    collisions.Add($"{registration.Tool.Name}.{prop.Name} -> {canonical}");
            }
        }

        Assert.True(
            collisions.Count == 0,
            "Declared tool parameters collide with meta fields: " + string.Join(", ", collisions));
    }

    // A tool (e.g. an MCP server) that declares a REAL parameter colliding with a
    // meta name. Schema-aware resolution must forward that parameter, not hijack it.
    private sealed class TimeoutParamTool(string schema) : INetclawTool
    {
        public string Name => "fake_timeout_tool";
        public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical("fake_timeout_tool");
        public string Description => "";
        public string GrantCategory => "test";
        public JsonElement ParameterSchema { get; } = JsonDocument.Parse(schema).RootElement.Clone();
        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            ToolInvocationContext context,
            CancellationToken ct = default)
            => Task.FromResult("ok");
        public AITool ToAITool() => AIFunctionFactory.Create(() => "ok", Name);
    }

    [Fact]
    public void ResolveMetaField_yields_to_a_declared_parameter_of_the_same_name()
    {
        // An MCP tool whose server declares a real "timeout" param, alongside the
        // injected meta fields.
        var tool = new TimeoutParamTool(
            """{"type":"object","properties":{"url":{"type":"string"},"timeout":{"type":"integer"},"_rationale":{"type":"string"},"_timeout_seconds":{"type":"integer"}}}""");

        // The server's own "timeout" is forwarded, NOT hijacked as the meta hint.
        Assert.Null(ToolArgumentValidator.ResolveMetaField(tool, "timeout"));

        // The exact injected meta key is always meta.
        Assert.Equal("_timeout_seconds", ToolArgumentValidator.ResolveMetaField(tool, "_timeout_seconds"));

        // A near-miss that is NOT a declared parameter still resolves to meta.
        Assert.Equal("_timeout_seconds", ToolArgumentValidator.ResolveMetaField(tool, "TimeoutSeconds"));
        Assert.Equal("_rationale", ToolArgumentValidator.ResolveMetaField(tool, "Rationale"));
    }

    [Fact]
    public void ResolveMetaField_resolves_near_miss_when_no_colliding_parameter()
    {
        // Same tool minus the real "timeout" param: bare "timeout" is now free to
        // resolve to the meta hint.
        var tool = new TimeoutParamTool(
            """{"type":"object","properties":{"url":{"type":"string"},"_timeout_seconds":{"type":"integer"}}}""");

        Assert.Equal("_timeout_seconds", ToolArgumentValidator.ResolveMetaField(tool, "timeout"));
        Assert.Null(ToolArgumentValidator.ResolveMetaField(tool, "url"));
    }
}
