// -----------------------------------------------------------------------
// <copyright file="MetaValidationAndNoticeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

/// <summary>
/// Meta-value validation rejects present-but-invalid meta values pre-dispatch,
/// and timeout overrides (clamp/floor) surface as model-facing notices in the
/// tool result — the silent clamp manufactured a false belief in production
/// (tool-call-metadata spec deltas).
/// </summary>
public sealed class MetaValidationAndNoticeTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    private static TurnContext InteractiveTurnContext(SessionId sessionId) => new()
    {
        SessionId = sessionId,
        TurnId = new TurnId("test-turn"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.SignalR,
        RequesterSenderId = new SenderId("local-user"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        SupportsInteractiveApproval = true
    };

    private async Task<ToolExecutionCompleted> RunPipelineAsync(
        IToolExecutor executor,
        Dictionary<string, object?> args,
        TimeSpan? timeout = null)
    {
        var probe = CreateTestProbe();
        var sessionId = new SessionId("D1/meta-validation-test");
        var toolCalls = new List<FunctionCallContent>
        {
            new("call-1", "shell_execute", args)
        };

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            toolCalls,
            sessionId,
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: Path.GetTempPath(),
            maxInlineToolResultChars: 4096,
            timeout: timeout ?? TimeSpan.FromSeconds(60),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            turnContext: InteractiveTurnContext(sessionId),
            ct: TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return completed;
    }

    private sealed class EchoExecutor(string result = "ok") : IToolExecutor
    {
        public int Invocations;
        public ToolExecutionContext? LastContext;

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        // Mirror the real executor's registry-free pre-dispatch validation
        // (sentinel + meta values) so the pipeline rejects the same calls it
        // would in production. The schema/unknown-key half needs a registry and
        // is covered by ToolArgumentValidatorTests against the real executor.
        public ToolArgumentRejection? ValidateToolCall(FunctionCallContent toolCall)
            => DispatchingToolExecutor.ValidateArguments(toolCall.Arguments);

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            Invocations++;
            LastContext = context;
            return Task.FromResult(result);
        }
    }

    // ── Timeout hint is honored exactly (no clamp, no floor) ──

    [Fact]
    public async Task High_timeout_request_honored_exactly()
    {
        // The agent's judgement governs: a large value is used as-is, not
        // clamped to a ceiling, and nothing is appended to the result.
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = 1200
        });

        var content = completed.ToolResults[0].Content;
        Assert.Equal(1, executor.Invocations);
        Assert.Equal(1200, executor.LastContext?.RequestedTimeoutSeconds);
        Assert.DoesNotContain("clamped", content);
        Assert.DoesNotContain("[timeout", content);
        Assert.Contains("ok", content);
    }

    [Fact]
    public async Task Low_timeout_request_honored_exactly()
    {
        // A value below the inherited default is used as-is — a shorter timeout
        // is the agent's prerogative and is strictly safer; no floor is imposed.
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = 10
        });

        var content = completed.ToolResults[0].Content;
        Assert.Equal(1, executor.Invocations);
        Assert.Equal(10, executor.LastContext?.RequestedTimeoutSeconds);
        Assert.DoesNotContain("[timeout", content);
    }

    [Fact]
    public async Task Integral_decimal_json_timeout_accepted_and_executes()
    {
        // {"_timeout_seconds": 300.0} — a common LLM emission — must be accepted
        // (TryGetInt32 rejects "300.0"; the integral-double fallback rescues it),
        // not rejected as invalid.
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = JsonDocument.Parse("300.0").RootElement.Clone()
        });

        Assert.Equal(1, executor.Invocations);
        Assert.Equal(300, executor.LastContext?.RequestedTimeoutSeconds);
        Assert.DoesNotContain("NOT executed", completed.ToolResults[0].Content);
    }

    [Fact]
    public async Task Honored_request_executes_with_no_notice()
    {
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = 300
        });

        var content = completed.ToolResults[0].Content;
        Assert.Equal(1, executor.Invocations);
        Assert.Equal(300, executor.LastContext?.RequestedTimeoutSeconds);
        Assert.DoesNotContain("[timeout", content);
        Assert.Equal("ok", content);
    }

    // ── Malformed meta values reject pre-dispatch ──

    [Fact]
    public async Task Unparseable_timeout_value_rejects_without_dispatch()
    {
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = "1200ms"
        });

        var content = completed.ToolResults[0].Content;
        Assert.Equal(0, executor.Invocations);
        Assert.Contains("'_timeout_seconds'", content);
        Assert.Contains("'1200ms'", content);
        Assert.Contains("positive integer", content);
        Assert.Contains("NOT executed", content);
    }

    [Fact]
    public async Task Non_boolean_background_value_rejects_without_dispatch()
    {
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_background"] = "yes"
        });

        var content = completed.ToolResults[0].Content;
        Assert.Equal(0, executor.Invocations);
        Assert.Contains("'_background'", content);
        Assert.Contains("boolean", content);
        Assert.Contains("NOT executed", content);
    }

    [Fact]
    public async Task Non_integral_json_timeout_rejects_without_uncaught_throw()
    {
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = JsonDocument.Parse("12.5").RootElement.Clone()
        });

        var content = completed.ToolResults[0].Content;
        Assert.Equal(0, executor.Invocations);
        Assert.Contains("'_timeout_seconds'", content);
        Assert.Contains("12.5", content);
    }

    [Fact]
    public async Task Negative_timeout_rejects_without_dispatch()
    {
        var executor = new EchoExecutor();
        var completed = await RunPipelineAsync(executor, new Dictionary<string, object?>
        {
            ["Command"] = "echo hi",
            ["_timeout_seconds"] = -5
        });

        Assert.Equal(0, executor.Invocations);
        Assert.Contains("NOT executed", completed.ToolResults[0].Content);
    }

    // ── Provider-boundary args parse failure ──

    [Fact]
    public async Task Args_parse_error_sentinel_rejects_without_dispatch_and_is_deterministic()
    {
        var executor = new EchoExecutor();
        var args = new Dictionary<string, object?>
        {
            [ToolCallArgumentErrors.ArgsParseErrorKey] =
                "Expected end of object. Raw arguments prefix: {\"Command\":\"ech"
        };

        var first = await RunPipelineAsync(executor, args);
        // Same call re-driven (persistence recovery replays the same args) —
        // the rejection must be deterministic.
        var second = await RunPipelineAsync(executor, args);

        Assert.Equal(0, executor.Invocations);
        var content = first.ToolResults[0].Content;
        Assert.Contains("not valid JSON", content);
        Assert.Contains("NOT executed", content);
        Assert.Contains("Raw arguments prefix:", content);
        Assert.Equal(content, second.ToolResults[0].Content);
    }

}
