// -----------------------------------------------------------------------
// <copyright file="BackgroundChildStampedAcceptanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

public sealed class BackgroundChildStampedAcceptanceTests(ITestOutputHelper output) : TestKit(output: output), IAsyncDisposable
{
    private readonly TestSessionTempDirectory _directory = TestSessionTempDirectory.Create("netclaw-stamped-acceptance-");
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    [Theory]
    [InlineData("requester")]
    [InlineData("call")]
    [InlineData("operation")]
    [InlineData("valid")]
    public async Task The_actual_pipeline_checks_the_owner_reply_against_the_stamped_start_before_returning_acceptance(string variant)
    {
        const string callId = "canonical-start-call";
        var session = new SessionId("signalr/stamped-start-owner");
        var context = TurnContext.FromMessageSource(session, new TurnId("original-turn"), new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId("original-requester"),
            Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        });
        var registry = new ToolRegistry();
        registry.RegisterCore(new FakeNetclawTool("file_read", "unused", "builtin"));
        var policy = new ToolAccessPolicy(_directory.Paths, new ToolConfig(),
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var client = new FakeChatClient();
        var snapshots = new ControllableWorkingContextSnapshotProvider();
        var spawner = new SubAgentSpawner(new StampedClientProvider(client), registry, policy, null,
            new StaticSystemPromptProvider("Use the assigned tools."), snapshots, NullLogger<SubAgentSpawner>.Instance);
        var profiles = new SubAgentDefinitionRegistry();
        profiles.Register(new SubAgentProfile
        {
            Name = "worker", Description = "Complete the assigned task.", SystemPrompt = "Inspect the neutral fixture.",
            ModelRole = ModelRole.Compaction, ToolNames = ["file_read"], Visibility = SubAgentVisibility.UserFacing,
            EmitStructuredFindings = false
        });
        var executor = new StampedSpawnExecutor(new SpawnAgentTool(profiles, spawner, _directory.Paths));
        var probe = CreateTestProbe();
        BackgroundChildRun? returned = null;
        var requests = new List<StartBackgroundChildRun>();
        var pipeline = new SessionToolExecutionPipeline(executor, TimeProvider.System, NoLogger.Instance);
        var batch = new SessionToolBatch(context, new SessionToolRunEnvironment
        {
            RecurrenceTaskId = new TurnId("original-task"),
            Storage = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(_directory.Path)),
            InlineOutputBudget = InlineOutputBudget.Default,
            SpawnChildActor = (payload, _, _) =>
            {
                var request = Assert.IsType<StartBackgroundChildRun>(payload);
                requests.Add(request);
                Assert.True(SessionState.SameCanonicalContext(context.ToRecord(), request.InvocationContext));
                Assert.Equal(new ChildRunStartKey.Tool(new ToolCallId(callId))
                { SessionId = session, TurnId = context.TurnId }, request.StartKey);
                Assert.Equal("spawn_agent", request.SourceOperation);
                var id = new SubAgentRunId("existing-recorded-run");
                Assert.NotEqual(id, request.Prepared.RunId);
                var run = new BackgroundChildRun
                {
                    RunId = id, AgentName = request.Prepared.AgentName,
                    ScopeId = new SubAgentScopeId($"{session.Value}/subagent/{request.Prepared.AgentName.Value}/{id.Value}"),
                    ArgumentsDigest = request.Prepared.ArgumentsDigest, StartKey = request.StartKey,
                    SourceOperation = request.SourceOperation, OriginalContext = request.InvocationContext,
                    OriginInputIds = [new InputId("original-input")],
                    InitialWorkingSnapshot = request.Prepared.Execution.Scope.InitialWorkingSnapshot,
                    ParentCheckpoint = new ToolLoopCheckpoint { TaskId = "original-task" },
                    AcceptedAtMs = 1, StartedAtMs = 2
                };
                returned = variant switch
                {
                    "requester" => run with { OriginalContext = run.OriginalContext with { RequesterSenderId = new SenderId("foreign-requester") } },
                    "call" => run with { StartKey = new ChildRunStartKey.Tool(new ToolCallId("foreign-start-call"))
                        { SessionId = session, TurnId = context.TurnId } },
                    "operation" => run with { SourceOperation = "skill_load" },
                    "valid" => run,
                    _ => throw new ArgumentOutOfRangeException(nameof(variant))
                };
                // These replies are internally valid. Only the actual stamped wrapper can reject their correlation.
                returned.Validate();
                return Task.FromResult<object>(new ChildStartReply.Accepted(returned, BackgroundChildState.Running));
            }
        })
        {
            ToolCalls = [new FunctionCallContent(callId, "spawn_agent", new Dictionary<string, object?>
                { ["Agent"] = "worker", ["Task"] = "Inspect the neutral fixture.", ["_rationale"] = "Delegate the assigned task." })],
            DefaultTimeout = new ToolExecutionTimeout(Ceiling), ReplyTo = probe.Ref, EmitSubAgentOutput = _ => { },
            ApprovalRequests = new ToolApprovalRequests(new ApprovalChannel(), _ => { }, new ToolExecutionTimeout(Timeout.InfiniteTimeSpan)),
            BackgroundJobs = new BackgroundJobDispatch.Unavailable(), CancellationToken = TestContext.Current.CancellationToken
        };
        await pipeline.ExecuteAsync(batch);
        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(requests);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(0, client.CallCount);
        Assert.NotNull(returned);
        var result = Assert.Single(completed.ToolResults);
        Assert.Equal(new ToolCallId(callId), result.ToolCallId);
        var receipt = Assert.Single(completed.ToolReceipts);
        Assert.Equal(callId, receipt.Key);
        Assert.Same(executor.Outputs!.Receipt, receipt.Value);
        Assert.Empty(completed.CompletedSubAgentRuns);
        if (variant == "valid")
        {
            Assert.IsType<ToolInvocationReceipt.Succeeded>(receipt.Value);
            using var body = JsonDocument.Parse(result.Content);
            Assert.Equal(returned.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(returned.ScopeId.Value, body.RootElement.GetProperty("scope_id").GetString());
            Assert.Equal("Running", body.RootElement.GetProperty("state").GetString());
            Assert.Equal("check_agent_run", body.RootElement.GetProperty("control_tool").GetString());
            return;
        }
        Assert.IsType<ToolInvocationReceipt.OtherOutcome>(receipt.Value);
        Assert.Equal(ToolInvocationOutcomeCategory.TransientFailure, receipt.Value.Category);
        Assert.StartsWith("Error executing tool:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(returned.RunId.Value, result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"run_id\"", result.Content, StringComparison.Ordinal);
    }

    private sealed class StampedSpawnExecutor(SpawnAgentTool tool) : IToolExecutor
    {
        public int CallCount { get; private set; }
        public ToolExecutionOutputs? Outputs { get; private set; }
        public Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            CallCount++;
            Outputs = context.Outputs;
            return tool.ExecuteAsync(call.Arguments, context.Invocation, ct);
        }
    }

    private sealed class StampedClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { await _directory.DisposeAsync(); }
    }
}
