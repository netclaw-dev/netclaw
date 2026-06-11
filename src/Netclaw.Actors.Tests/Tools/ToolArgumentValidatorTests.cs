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

    public ToolArgumentValidatorTests()
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Auto
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config);
        _executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));
    }

    private static ToolExecutionContext PersonalContext(string sessionDir)
        => new("signalr/thread-1", sessionDir)
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        };

    private async Task<string> ExecuteShellAsync(IDictionary<string, object?> args)
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "nc-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);
        try
        {
            var toolCall = new FunctionCallContent("call-1", "shell_execute", args);
            return await _executor.ExecuteAsync(
                toolCall, PersonalContext(sessionDir), TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task TimeoutSeconds_rejected_with_meta_key_suggestion()
    {
        // The literal arg shape from production session
        // D0AC6CKBK5K_1781115410_840529 that was silently dropped.
        var result = await ExecuteShellAsync(new Dictionary<string, object?>
        {
            ["Command"] = "echo should-not-run",
            ["TimeoutSeconds"] = "1200"
        });

        Assert.Contains("Unrecognized argument 'TimeoutSeconds'", result);
        Assert.Contains("Did you mean '_timeout_seconds'?", result);
        Assert.Contains("NOT executed", result);
        Assert.DoesNotContain("should-not-run", result);
    }

    [Fact]
    public async Task Underscore_missing_timeout_seconds_rejected_never_bound()
    {
        var result = await ExecuteShellAsync(new Dictionary<string, object?>
        {
            ["Command"] = "echo should-not-run",
            ["timeout_seconds"] = 300
        });

        Assert.Contains("Unrecognized argument 'timeout_seconds'", result);
        Assert.Contains("Did you mean '_timeout_seconds'?", result);
        Assert.DoesNotContain("should-not-run", result);
    }

    [Fact]
    public async Task Lowercase_declared_param_still_accepted()
    {
        // Deterministic canonicalization for declared params is existing
        // consumption behavior (Qwen text-parser path emits lowercase keys).
        var result = await ExecuteShellAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo flexible-ok"
        });

        Assert.DoesNotContain("Unrecognized argument", result);
        Assert.Contains("flexible-ok", result);
    }

    [Fact]
    public async Task Exact_meta_key_accepted()
    {
        var result = await ExecuteShellAsync(new Dictionary<string, object?>
        {
            ["Command"] = "echo meta-ok",
            ["_timeout_seconds"] = 120,
            ["_rationale"] = "test"
        });

        Assert.DoesNotContain("Unrecognized argument", result);
        Assert.Contains("meta-ok", result);
    }

    [Fact]
    public async Task Wholly_unknown_key_rejected_without_suggestion_lists_valid_args()
    {
        var result = await ExecuteShellAsync(new Dictionary<string, object?>
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
    public async Task Typo_in_declared_param_rejected_with_suggestion()
    {
        var result = await ExecuteShellAsync(new Dictionary<string, object?>
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

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
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
        var executor = new DispatchingToolExecutor(registry);

        string result;
        try
        {
            result = await executor.ExecuteAsync(
                new FunctionCallContent("call-mcp", "memorizer/store", new Dictionary<string, object?>
                {
                    ["TotallyUnknownKey"] = "value"
                }),
                ct: TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            // An authorization/invocation failure is still past the key gate.
            result = ex.Message;
        }

        Assert.DoesNotContain("Unrecognized argument", result);
    }
}
