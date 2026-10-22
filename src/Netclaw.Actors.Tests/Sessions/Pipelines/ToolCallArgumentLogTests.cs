// -----------------------------------------------------------------------
// <copyright file="ToolCallArgumentLogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

/// <summary>
/// Issue #2589: each tool call writes one Info line with its redacted, bounded
/// arguments and its rationale. The OTLP exporter sends this line, so it must
/// not hold secrets or large payloads.
/// </summary>
public sealed class ToolCallArgumentLogTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const string InvokingPrefix = "Invoking tool [";
    private const string StartedPrefix = "Tool authorization attempt started";
    private const string Redacted = "***REDACTED***";

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon("akka.loglevel = INFO", HoconAddMode.Prepend);
    }

    [Fact]
    public async Task Tool_call_logs_arguments_and_rationale_with_attempt_id()
    {
        const string rationale = "List the app files to find the config.";
        var executor = new RecordingRationaleExecutor();
        var run = await RunAsync(executor, new Dictionary<string, object?>
        {
            ["command"] = "ls -la /srv/app",
            ["_rationale"] = rationale
        });

        var invoked = run.Invoked;
        Assert.Equal("shell_execute", invoked["ToolName"]?.ToString());
        Assert.Equal("call-1", invoked["CallId"]?.ToString());
        Assert.Contains("ls -la /srv/app", invoked["Args"]?.ToString(), StringComparison.Ordinal);
        Assert.Equal(rationale, invoked["Rationale"]?.ToString());
        Assert.Equal(run.SessionId.Value, invoked["SessionId"]?.ToString());
        Assert.Equal(
            run.Started["AuthorizationAttemptId"]?.ToString(),
            invoked["AuthorizationAttemptId"]?.ToString());
        Assert.Equal(
            run.Result.AuthorizationAttemptId.Value,
            invoked["AuthorizationAttemptId"]?.ToString());
    }

    [Fact]
    public async Task Tool_call_argument_log_redacts_secrets()
    {
        const string bearer = "bearer-raw-abc123";
        const string apiKey = "apikey-raw-456";
        const string sensitive = "sens-raw-789";
        var executor = new RecordingRationaleExecutor();
        var run = await RunAsync(executor, new Dictionary<string, object?>
        {
            ["command"] = $"curl -H 'Authorization: Bearer {bearer}' https://example.test/items",
            ["api_key"] = apiKey,
            ["header"] = new SensitiveString(sensitive),
            ["_rationale"] = "Call the item API."
        });

        var args = run.Invoked["Args"]?.ToString();
        Assert.NotNull(args);
        Assert.Contains(Redacted, args, StringComparison.Ordinal);
        foreach (var raw in new[] { bearer, apiKey, sensitive })
        {
            Assert.DoesNotContain(raw, args, StringComparison.Ordinal);
            Assert.DoesNotContain(raw, run.InvokedLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Tool_call_argument_log_hashes_large_content_and_keeps_rationale()
    {
        // The content comes before _rationale. A truncated Args would lose the
        // rationale, so the rationale has its own field.
        var content = new string('x', 100_000);
        var rationale = "Write the generated report so that the operator can review it. " +
                        "The report holds the full audit output for this release.";
        var executor = new RecordingRationaleExecutor();
        var run = await RunAsync(executor, new Dictionary<string, object?>
        {
            ["path"] = "report.txt",
            ["content"] = content,
            ["_rationale"] = rationale
        }, toolName: "file_write");

        var args = run.Invoked["Args"]?.ToString();
        Assert.NotNull(args);
        Assert.Contains(HashForm(content), args, StringComparison.Ordinal);
        Assert.Contains("report.txt", args, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 20), run.InvokedLine, StringComparison.Ordinal);
        Assert.True(args.Length <= 4_099, $"Args has {args.Length} chars.");
        Assert.Equal(rationale, run.Invoked["Rationale"]?.ToString());
    }

    [Fact]
    public async Task Rejected_tool_call_still_logs_arguments()
    {
        var executor = new RecordingRationaleExecutor();
        var run = await RunAsync(executor, new Dictionary<string, object?>
        {
            ["command"] = "rm -rf /srv/app/cache"
        });

        Assert.Equal("invalid_rationale", run.Result.FailureCode);
        Assert.Equal(0, executor.ExecutionCount);
        Assert.Contains("rm -rf /srv/app/cache", run.Invoked["Args"]?.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Assert.IsType<string>(run.Invoked["Rationale"]));
        Assert.Equal(
            run.Started["AuthorizationAttemptId"]?.ToString(),
            run.Invoked["AuthorizationAttemptId"]?.ToString());
    }

    [Fact]
    public async Task Logged_arguments_do_not_change_tool_input()
    {
        // Negative test: the log helper redacts and hashes a copy. The tool must
        // receive the original values. The real executor removes _rationale
        // before the tool runs, so the tool does not get that key.
        const string apiKey = "raw-key-value-55";
        var content = new string('c', 1_000);
        using var temp = new DisposableTempDir();
        var tool = new FakeNetclawTool("probe_write", "written");
        var registry = new ToolRegistry();
        registry.Register(tool);
        var executor = new DispatchingToolExecutor(registry, new ToolAccessPolicy(
            new NetclawPaths(temp.Path),
            new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([])));
        var probe = CreateTestProbe();
        var sessionId = new SessionId("D1/tool-arg-log-input");
        var calls = new List<FunctionCallContent>
        {
            new("call-1", tool.Name, new Dictionary<string, object?>
            {
                ["api_key"] = apiKey,
                ["content"] = content,
                ["_rationale"] = "Store the key for the deploy step."
            })
        };

        var pipeline = new SessionToolPipelineTestFixture(executor, calls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .InSessionDirectory(temp.Path)
            .WithLogger(Logging.GetLogger(Sys, typeof(ToolCallArgumentLogTests)))
            .ExecuteAsync(TestContext.Current.CancellationToken);
        var results = await StreamedToolReplyProbe.ExpectAsync(
            probe, resultCount: 1, timeout: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(tool.WasCalled, Assert.Single(results).Message.Content);
        var received = tool.LastArguments;
        Assert.NotNull(received);
        Assert.False(received.ContainsKey("_rationale"));
        Assert.Equal(apiKey, received["api_key"]?.ToString());
        Assert.Equal(content, received["content"]?.ToString());
    }

    [Fact]
    public async Task Tool_call_with_a_throwing_argument_still_runs_and_its_sibling_succeeds()
    {
        // Review finding 3: the log line comes before the tool runs. An exception
        // from the formatter must not fail this call or the other calls in the batch.
        var executor = new RecordingRationaleExecutor();
        var calls = new List<FunctionCallContent>
        {
            new("call-throwing", "probe_list", new Dictionary<string, object?>
            {
                ["items"] = new ThrowingArgumentList(),
                ["_rationale"] = "Read the items."
            }),
            new("call-sibling", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "echo ok",
                ["_rationale"] = "Print ok."
            })
        };

        var results = await ToolArgumentLogPipeline.RunBatchAsync(this, executor, calls, "D1/tool-arg-log-throwing");

        Assert.Equal(2, results.Count);
        Assert.Equal(
            ["call-sibling", "call-throwing"],
            executor.Received.Select(static call => call.CallId).Order(StringComparer.Ordinal));
        Assert.All(results, result =>
            Assert.Contains($"executed:{result.Message.ToolCallId?.Value}", result.Message.Content, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Info_line_reads_argument_values_when_info_is_enabled()
    {
        // Control for ToolCallArgumentLogLevelTests: when Info is on, the
        // formatter reads the list, so the read counter can see the formatter.
        var items = new CountingArgumentList("first", "second");
        var calls = new List<FunctionCallContent>
        {
            new("call-1", "probe_list", new Dictionary<string, object?>
            {
                ["items"] = items,
                ["_rationale"] = "Read the items."
            })
        };

        await ToolArgumentLogPipeline.RunBatchAsync(
            this, new RecordingRationaleExecutor(), calls, "D1/tool-arg-log-info-on");

        Assert.True(items.Reads > 0, "The formatter did not read the argument list.");
    }

    private async Task<PipelineRun> RunAsync(
        IToolExecutor executor,
        Dictionary<string, object?> args,
        string toolName = "shell_execute")
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = CreateTestProbe();
        var diagnostics = CreateTestProbe();
        Sys.EventStream.Subscribe(diagnostics.Ref, typeof(Info));
        var sessionId = new SessionId("D1/tool-arg-log");
        var calls = new List<FunctionCallContent> { new("call-1", toolName, args) };

        IReadOnlyList<ToolCallResult> results = [];
        await EventFilter.Info(contains: InvokingPrefix).ExpectAsync(1, async () =>
        {
            var pipeline = new SessionToolPipelineTestFixture(executor, calls, sessionId, probe.Ref)
                .WithTurnContext(InteractiveTurnContext(sessionId))
                .WithLogger(Logging.GetLogger(Sys, typeof(ToolCallArgumentLogTests)))
                .ExecuteAsync(cancellationToken);
            results = await StreamedToolReplyProbe.ExpectAsync(
                probe, resultCount: 1, timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken);
            await pipeline.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }, cancellationToken: cancellationToken);

        var started = await FishAsync(diagnostics, StartedPrefix, cancellationToken);
        var invoked = await FishAsync(diagnostics, InvokingPrefix, cancellationToken);
        return new PipelineRun(
            Assert.Single(results),
            sessionId,
            started.GetProperties(),
            invoked.GetProperties(),
            invoked.ToString() ?? string.Empty);
    }

    private static async Task<LogMessage> FishAsync(
        TestProbe diagnostics,
        string prefix,
        CancellationToken cancellationToken)
    {
        var info = await diagnostics.FishForMessageAsync<Info>(
            message => message.Message.ToString()!.StartsWith(prefix, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken);
        return Assert.IsAssignableFrom<LogMessage>(info.Message);
    }

    private static string HashForm(string value)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return $"<{value.Length} chars, sha256:{hash[..16]}>";
    }

    internal static TurnContext InteractiveTurnContext(SessionId sessionId) => new()
    {
        SessionId = sessionId,
        TurnId = new TurnId("tool-arg-log-turn"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.SignalR,
        RequesterSenderId = new SenderId("local-user"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        SupportsInteractiveApproval = true
    };

    private sealed record PipelineRun(
        ToolCallResult Result,
        SessionId SessionId,
        IReadOnlyDictionary<string, object?> Started,
        IReadOnlyDictionary<string, object?> Invoked,
        string InvokedLine);

    /// <summary>
    /// Requires _rationale as the real executor does, and records the call that
    /// the pipeline gives to the executor.
    /// </summary>
    internal sealed class RecordingRationaleExecutor : IToolExecutor
    {
        private readonly List<FunctionCallContent> _received = [];

        public IReadOnlyList<FunctionCallContent> Received
        {
            get
            {
                lock (_received)
                    return _received.ToArray();
            }
        }

        public int ExecutionCount
        {
            get
            {
                lock (_received)
                    return _received.Count;
            }
        }

        public ToolArgumentRejection? ValidateToolCall(FunctionCallContent toolCall)
            => ToolCallMetaExtractor.ValidateRequiredRationale(
                toolCall.Arguments,
                ToolCallMeta.ResolveExactMetaField) is { } error
                ? new ToolArgumentRejection(error, "invalid_rationale")
                : null;

        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext context,
            CancellationToken ct = default)
        {
            lock (_received)
                _received.Add(toolCall);
            return Task.FromResult($"executed:{toolCall.CallId}");
        }
    }
}

/// <summary>
/// Review finding 5: when Info is off, the pipeline must not format the
/// arguments. The read counter on an argument list shows whether the formatter
/// ran. The pipeline itself does not read the list.
/// </summary>
public sealed class ToolCallArgumentLogLevelTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon("akka.loglevel = WARNING", HoconAddMode.Prepend);
    }

    [Fact]
    public async Task Tool_call_does_not_format_arguments_when_info_is_disabled()
    {
        Assert.False(Logging.GetLogger(Sys, typeof(ToolCallArgumentLogLevelTests)).IsInfoEnabled);
        var items = new CountingArgumentList("first", "second");
        var calls = new List<FunctionCallContent>
        {
            new("call-1", "probe_list", new Dictionary<string, object?>
            {
                ["items"] = items,
                ["_rationale"] = "Read the items."
            })
        };

        var results = await ToolArgumentLogPipeline.RunBatchAsync(
            this, new ToolCallArgumentLogTests.RecordingRationaleExecutor(), calls, "D1/tool-arg-log-info-off");

        Assert.Contains("executed:call-1", Assert.Single(results).Message.Content, StringComparison.Ordinal);
        Assert.Equal(0, items.Reads);
    }
}

internal static class ToolArgumentLogPipeline
{
    public static async Task<IReadOnlyList<ToolCallResult>> RunBatchAsync(
        TestKitBase kit,
        IToolExecutor executor,
        IReadOnlyList<FunctionCallContent> calls,
        string sessionName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = kit.CreateTestProbe();
        var sessionId = new SessionId(sessionName);
        var pipeline = new SessionToolPipelineTestFixture(executor, calls, sessionId, probe.Ref)
            .WithTurnContext(ToolCallArgumentLogTests.InteractiveTurnContext(sessionId))
            .WithLogger(Logging.GetLogger(kit.Sys, typeof(ToolArgumentLogPipeline)))
            .ExecuteAsync(cancellationToken);
        var results = await StreamedToolReplyProbe.ExpectAsync(
            probe, resultCount: calls.Count, timeout: TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken);
        await pipeline.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        return results;
    }
}

/// <summary>An argument list that counts each read of its items.</summary>
internal sealed class CountingArgumentList : System.Collections.ArrayList
{
    private int _reads;

    public CountingArgumentList(params object?[] items)
    {
        foreach (var item in items)
            base.Add(item);
    }

    public int Reads => Volatile.Read(ref _reads);

    public override System.Collections.IEnumerator GetEnumerator()
    {
        Interlocked.Increment(ref _reads);
        return base.GetEnumerator();
    }

    public override object? this[int index]
    {
        get
        {
            Interlocked.Increment(ref _reads);
            return base[index];
        }
        set => base[index] = value;
    }

    public override void CopyTo(Array array, int arrayIndex)
    {
        Interlocked.Increment(ref _reads);
        base.CopyTo(array, arrayIndex);
    }

    public override object?[] ToArray()
    {
        Interlocked.Increment(ref _reads);
        return base.ToArray();
    }
}

/// <summary>An argument list whose items cannot be read.</summary>
internal sealed class ThrowingArgumentList : System.Collections.ArrayList
{
    public ThrowingArgumentList()
    {
        base.Add("throwing-raw-item");
    }

    public override System.Collections.IEnumerator GetEnumerator()
        => throw new InvalidOperationException("The enumerator throws.");

    public override object? this[int index]
    {
        get => throw new InvalidOperationException("The indexer throws.");
        set => throw new NotSupportedException();
    }

    public override void CopyTo(Array array, int arrayIndex) => throw new InvalidOperationException("CopyTo throws.");

    public override object?[] ToArray() => throw new InvalidOperationException("ToArray throws.");
}
