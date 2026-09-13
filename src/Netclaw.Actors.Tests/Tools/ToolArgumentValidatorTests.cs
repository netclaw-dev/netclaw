// -----------------------------------------------------------------------
// <copyright file="ToolArgumentValidatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Unknown-key validation at the dispatcher: near-miss keys are rejected with
/// a suggestion (never silently bound — the original production bug passed
/// "TimeoutSeconds" and got a silent 90s default), declared-param flexible
/// binding is preserved, and exact meta keys pass through.
/// </summary>
public class ToolArgumentValidatorTests
{
    private readonly DispatchingToolExecutor _executor;
    private readonly ShellTool _shellTool;

    public ToolArgumentValidatorTests()
    {
        var environment = TestShellEnvironment.Current;
        var commandPolicy = new ShellCommandPolicy(environment);
        var pathPolicy = new ToolPathPolicy(environment, []);
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Auto
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(TestToolAccessPolicy.Create(config, commandPolicy, pathPolicy));
        _shellTool = Assert.IsType<ShellTool>(registry.GetByName(ShellTool.ToolName));
        _executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(new NetclawPaths(),
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                commandPolicy,
                pathPolicy));
    }

    // This suite tests dispatcher and binder contracts. ShellToolTests owns native process coverage.
    private ToolCallInterpretation InterpretShellCall(IDictionary<string, object?> args)
    {
        var callArgs = new Dictionary<string, object?>(args, StringComparer.Ordinal);
        if (!callArgs.Keys.Any(key =>
                string.Equals(ToolArgumentHelper.ResolveMetaField(key), "_rationale", StringComparison.Ordinal)))
            callArgs["_rationale"] = "Validate the tool argument contract.";

        return _executor.InterpretToolCall(
            new FunctionCallContent("call-1", ShellTool.ToolName, callArgs));
    }

    private (ToolCallMeta? Meta, ShellTool.Params Arguments) InterpretAcceptedShellCall(
        IDictionary<string, object?> args)
    {
        var interpretation = InterpretShellCall(args);
        Assert.Null(interpretation.Rejection);
        var cleanedArguments = Assert.IsAssignableFrom<IDictionary<string, object?>>(
            interpretation.Cleaned.Arguments);
        return (interpretation.Meta, _shellTool.ParseArguments(cleanedArguments));
    }

    private string InterpretRejectedShellCall(IDictionary<string, object?> args)
    {
        var interpretation = InterpretShellCall(args);
        return Assert.IsType<ToolArgumentRejection>(interpretation.Rejection).Message;
    }

    [Fact]
    public void Missing_rationale_rejects_before_execution()
    {
        var rejection = _executor.ValidateToolCall(new FunctionCallContent(
            "call-missing-rationale",
            "shell_execute",
            new Dictionary<string, object?> { ["Command"] = "echo should-not-run" }));

        Assert.NotNull(rejection);
        Assert.Equal("invalid_rationale", rejection!.DenyReason);
        Assert.Contains("'_rationale'", rejection.Message);
        Assert.Contains("non-empty string", rejection.Message);
        Assert.Contains("NOT executed", rejection.Message);
    }

    [Fact]
    public void Blank_null_and_non_string_rationales_reject()
    {
        object?[] invalidValues = [null, "  ", 42, false];

        foreach (var invalidValue in invalidValues)
        {
            var rejection = _executor.ValidateToolCall(new FunctionCallContent(
                "call-invalid-rationale",
                "shell_execute",
                new Dictionary<string, object?>
                {
                    ["Command"] = "echo should-not-run",
                    ["_rationale"] = invalidValue
                }));

            Assert.NotNull(rejection);
            Assert.Equal("invalid_rationale", rejection!.DenyReason);
        }
    }

    [Fact]
    public void TimeoutSeconds_accepted_and_consumed_as_meta_field()
    {
        // The literal arg shape from production session
        // D0AC6CKBK5K_1781115410_840529. ChatGPT-trained models (Qwen) emit the
        // underscore-dropped name; rather than reject (which pushed the model off
        // tools entirely — session D0AC6CKBK5K_1781746527), it now resolves onto
        // _timeout_seconds and the call runs. The dispatcher consumes the value.
        var (meta, arguments) = InterpretAcceptedShellCall(new Dictionary<string, object?>
        {
            ["Command"] = "echo runs-now",
            ["TimeoutSeconds"] = "1200"
        });

        Assert.Equal(1200, meta?.TimeoutHintSeconds);
        Assert.Equal("echo runs-now", arguments.Command);
    }

    [Fact]
    public void Underscore_missing_timeout_seconds_accepted()
    {
        var (meta, arguments) = InterpretAcceptedShellCall(new Dictionary<string, object?>
        {
            ["Command"] = "echo runs-now",
            ["timeout_seconds"] = 300
        });

        Assert.Equal(300, meta?.TimeoutHintSeconds);
        Assert.Equal("echo runs-now", arguments.Command);
    }

    [Fact]
    public void Conflicting_timeout_spellings_rejected_as_ambiguous()
    {
        // Two distinct keys resolving to the same meta field would force a silent
        // pick-one-drop-the-other — the no-silent-discard invariant rejects it.
        var result = InterpretRejectedShellCall(new Dictionary<string, object?>
        {
            ["Command"] = "echo should-not-run",
            ["_timeout_seconds"] = 120,
            ["TimeoutSeconds"] = 1200
        });

        Assert.Contains("both map to the meta field '_timeout_seconds'", result);
        Assert.Contains("NOT executed", result);
        Assert.DoesNotContain("should-not-run", result);
    }

    [Theory]
    [InlineData("Rationale")]
    [InlineData("rationale")]
    public void Misnamed_rationale_accepted(string key)
    {
        var (meta, arguments) = InterpretAcceptedShellCall(new Dictionary<string, object?>
        {
            ["Command"] = "echo runs-now",
            [key] = "because"
        });

        Assert.Equal("because", meta?.Rationale);
        Assert.Equal("echo runs-now", arguments.Command);
    }

    [Fact]
    public void Misnamed_timeout_with_invalid_value_rejected_loudly()
    {
        // Spelling tolerance must not become a silent escape hatch: a resolved
        // meta key with an unusable value is still rejected before dispatch,
        // naming the model's own key spelling.
        var result = InterpretRejectedShellCall(new Dictionary<string, object?>
        {
            ["Command"] = "echo should-not-run",
            ["TimeoutSeconds"] = "not-a-number"
        });

        Assert.Contains("Meta argument 'TimeoutSeconds'", result);
        Assert.Contains("not a valid positive integer", result);
        Assert.DoesNotContain("should-not-run", result);
    }

    [Fact]
    public void Lowercase_declared_param_still_accepted()
    {
        // Deterministic canonicalization for declared params is existing
        // consumption behavior (Qwen text-parser path emits lowercase keys).
        var (_, arguments) = InterpretAcceptedShellCall(new Dictionary<string, object?>
        {
            ["command"] = "echo flexible-ok"
        });

        Assert.Equal("echo flexible-ok", arguments.Command);
    }

    [Fact]
    public void Exact_meta_key_accepted()
    {
        var (meta, arguments) = InterpretAcceptedShellCall(new Dictionary<string, object?>
        {
            ["Command"] = "echo meta-ok",
            ["_timeout_seconds"] = 120,
            ["_rationale"] = "test"
        });

        Assert.Equal(120, meta?.TimeoutHintSeconds);
        Assert.Equal("test", meta?.Rationale);
        Assert.Equal("echo meta-ok", arguments.Command);
    }

    [Fact]
    public void Wholly_unknown_key_rejected_without_suggestion_lists_valid_args()
    {
        var result = InterpretRejectedShellCall(new Dictionary<string, object?>
        {
            ["Command"] = "echo should-not-run",
            ["Banana"] = true
        });

        Assert.Contains("Unrecognized argument 'Banana'", result);
        Assert.DoesNotContain("Did you mean", result);
        Assert.Contains("Valid arguments:", result);
        Assert.Contains("Command", result);
        Assert.Contains("_timeout_seconds", result);
        Assert.DoesNotContain("should-not-run", result);
    }

    [Fact]
    public void Typo_in_declared_param_rejected_with_suggestion()
    {
        var result = InterpretRejectedShellCall(new Dictionary<string, object?>
        {
            ["Comand"] = "echo should-not-run"
        });

        Assert.Contains("Unrecognized argument 'Comand'", result);
        Assert.Contains("Did you mean 'Command'?", result);
        Assert.DoesNotContain("should-not-run", result);
    }

    // A native tool that declares 'text' (like send_channel_message) but binds
    // an interchangeable 'Message' key at runtime — recognition must accept the
    // alias bidirectionally or a previously-working call shape regresses.
    private sealed class TextAliasTool : INetclawTool
    {
        public string Name => "fake_text_tool";
        public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical("fake_text_tool");
        public string Description => "";
        public string GrantCategory => "test";
        public System.Text.Json.JsonElement ParameterSchema { get; } =
            System.Text.Json.JsonDocument.Parse(
                """{"type":"object","properties":{"text":{"type":"string"},"_rationale":{"type":"string"}}}""")
                .RootElement.Clone();

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            ToolInvocationContext context,
            CancellationToken ct = default)
            => Task.FromResult("ok");

        // Not exercised by key validation, which reads only Name + ParameterSchema.
        public AITool ToAITool() => AIFunctionFactory.Create(() => "ok", Name);
    }

    [Fact]
    public void Declared_text_accepts_message_alias_and_vice_versa()
    {
        var tool = new TextAliasTool();

        // 'Message' is consumed by binding's text↔Message fallback even though
        // only 'text' is declared — it must not be rejected.
        Assert.Null(ToolArgumentValidator.ValidateArgumentKeys(tool, new Dictionary<string, object?>
        {
            ["Message"] = "hi",
            ["_rationale"] = "test"
        }));

        // The declared key itself still works.
        Assert.Null(ToolArgumentValidator.ValidateArgumentKeys(tool, new Dictionary<string, object?>
        {
            ["text"] = "hi"
        }));

        // A genuinely unknown key is still rejected.
        Assert.NotNull(ToolArgumentValidator.ValidateArgumentKeys(tool, new Dictionary<string, object?>
        {
            ["bogus"] = "x"
        }));
    }

    [Fact]
    public async Task Mcp_tools_exempt_from_native_validation()
    {
        // McpToolAdapter is skipped at the dispatcher gate: an extra unknown
        // key must NOT produce the native "Unrecognized argument" rejection —
        // the MCP server's own schema validation is the authority
        // (mcp-schema-coercion spec). The unknown-key gate runs BEFORE
        // authorization, so reaching any other outcome proves the exemption.
        var fakeTool = AIFunctionFactory.Create(() => "mcp-result", "store");
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(fakeTool, "memorizer", "store"));
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(new NetclawPaths(),
                new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])));

        string result;
        try
        {
            result = await executor.ExecuteAsync(
                new FunctionCallContent("call-mcp", "memorizer/store", new Dictionary<string, object?>
                {
                    ["TotallyUnknownKey"] = "value",
                    ["_rationale"] = "Verify the MCP validation boundary."
                }),
                TestToolExecutionContext.CreateUnbound(),
                ct: TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            // An authorization/invocation failure is still past the key gate.
            result = ex.Message;
        }

        Assert.DoesNotContain("Unrecognized argument", result);
    }

    [Fact]
    public void Mcp_conflicting_meta_spellings_rejected_as_ambiguous()
    {
        // MCP tools skip native key validation (above), but the no-silent-discard
        // invariant must still hold: two distinct keys mapping to one meta field are
        // rejected loudly. The guard lives in ValidateMetaValues (every tool), not
        // the native-only ValidateArgumentKeys — this proves it covers the MCP path.
        var fakeTool = AIFunctionFactory.Create(() => "mcp-result", "store");
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(fakeTool, "memorizer", "store"));
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(new NetclawPaths(),
                new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])));

        var rejection = executor.ValidateToolCall(new FunctionCallContent(
            "call-mcp", "memorizer/store", new Dictionary<string, object?>
            {
                ["_timeout_seconds"] = 120,
                ["TimeoutSeconds"] = 1200
            }));

        Assert.NotNull(rejection);
        Assert.Contains("both map to the meta field '_timeout_seconds'", rejection!.Message);
    }

    [Fact]
    public void InterpretToolCall_valid_extracts_meta_and_strips_keys()
    {
        var interp = _executor.InterpretToolCall(new FunctionCallContent("c", "shell_execute",
            new Dictionary<string, object?>
            {
                ["Command"] = "echo hi",
                ["TimeoutSeconds"] = 300,
                ["_rationale"] = "Verify meta extraction."
            }));

        Assert.Null(interp.Rejection);
        Assert.Equal(300, interp.Meta?.TimeoutHintSeconds);
        Assert.DoesNotContain("TimeoutSeconds", (IDictionary<string, object?>)interp.Cleaned.Arguments!);
    }

    [Fact]
    public void InterpretToolCall_rejection_leaves_the_call_uncleaned()
    {
        var original = new FunctionCallContent("c", "shell_execute", new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = 1,
            ["TimeoutSeconds"] = 2
        });

        var interp = _executor.InterpretToolCall(original);

        Assert.NotNull(interp.Rejection);
        Assert.Contains("both map to the meta field", interp.Rejection!.Message);
        Assert.Same(original, interp.Cleaned); // not cleaned when rejected
        Assert.Null(interp.Meta);
    }
}
