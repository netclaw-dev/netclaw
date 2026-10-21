// -----------------------------------------------------------------------
// <copyright file="ChildLifetimeTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Shared setup for specs about a live child run and an automated input (a reminder).
/// The base class gives the requester, the reminder, the session helpers, and a system prompt that names the audience.
/// A parent tool call and a child tool call both need an approval. Each tool counts its runs, so a spec can prove
/// that an approval stayed open. The child run waits on a gate until a spec releases it.
/// </summary>
public abstract class ChildLifetimeTestBase : ApprovalWaitTestBase
{
    protected const string FirstTool = "probe_first";
    protected const string SecondTool = "probe_second";

    // The reply text that FakeChatClient gives a child run that makes no tool call.
    protected const string ChildReplyText = "[fake] Response";
    protected static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private protected readonly ChildClients Clients;
    private protected readonly CountingTool First = new(FirstTool);
    private protected readonly CountingTool Second = new(SecondTool);

    protected ChildLifetimeTestBase(ITestOutputHelper output) : base(output)
    {
        // The parent model is the fake client of the base class, so the shared order and audience checks apply.
        Clients = new ChildClients(FakeChat);
    }

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // The base registers a single fake client, a tool executor, and a tool registry. The last registration wins,
    // so this method replaces them with the child-aware set.
    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        var registry = new ToolRegistry();
        var profiles = new SubAgentDefinitionRegistry();
        profiles.Register(new SubAgentProfile
        {
            Name = "child-a",
            Description = "child-a",
            SystemPrompt = "child-a",
            ModelRole = ModelRole.Fallback,
            ToolNames = [FirstTool],
            EmitStructuredFindings = false,
            Visibility = SubAgentVisibility.UserFacing
        });
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>
            {
                [FirstTool] = ToolApprovalMode.Approval,
                [SecondTool] = ToolApprovalMode.Approval
            }
        };
        var policy = new ToolAccessPolicy(TestPaths, toolConfig,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var prompt = new AudienceNamingPromptProvider();
        var spawner = new SubAgentSpawner(Clients, registry, policy, null, prompt,
            new PlainSnapshotProvider(), NullLogger<SubAgentSpawner>.Instance, new SessionConfig());
        registry.RegisterCore(new SpawnAgentTool(profiles, spawner, TestPaths));
        // The runtime refuses a spawn when the requester cannot read the result file.
        registry.RegisterCore(new FileReadTool(toolConfig, TestPaths, new ToolPathPolicy([])));
        registry.RegisterCore(First);
        registry.RegisterCore(Second);

        services.AddSingleton<IChatClientProvider>(Clients);
        services.AddSingleton(new ModelCapabilities { ModelId = "child-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            // A spec that needs passivation sends ReceiveTimeout itself. A timer would race the spec.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning { SnapshotInterval = 0, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(prompt);
        services.AddSingleton(registry);
        services.AddSingleton(profiles);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(registry, policy, null,
            NullLogger<DispatchingToolExecutor>.Instance));
    }

    protected async Task<Akka.TestKit.TestProbe> JoinFullAsync(SessionId sessionId)
    {
        var subscriber = CreateTestProbe("subscriber-" + Guid.NewGuid().ToString("N"));
        await JoinAsync(subscriber, sessionId, OutputFilter.Full);
        return subscriber;
    }

    protected Task SendRequesterAsync(SessionId sessionId, string content)
        => SendAsync(sessionId, content, RequesterSource(Requester));

    protected Task<ISessionResponse> ApproveAsync(SessionId sessionId, ToolCallId callId)
        => SessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = callId,
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(Requester)
        }, Deadline, Cancellation);

    protected static FunctionCallContent Spawn(string callId, string agent) => new(callId, "spawn_agent",
        new Dictionary<string, object?>
        {
            ["agent"] = agent,
            ["task"] = $"Complete {agent}.",
            ["_rationale"] = "Start a child run."
        });

    protected static FunctionCallContent Probe(string callId, string toolName) => new(callId, toolName,
        new Dictionary<string, object?> { ["_rationale"] = "Ask for an approval." });

    protected static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A restart makes a "child owner was lost" notice for a run that never answered. A real result is a notice for a
    // completed run whose result file holds the reply of the child.
    protected static bool HasChildResult(IReadOnlyList<ChatMessage> request)
        => request
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                              && message.Text.StartsWith("Child run ended.", StringComparison.Ordinal))
            .Select(message => message.Text)
            .Any(IsRealChildNotice);

    private static bool IsRealChildNotice(string notice)
    {
        if (!notice.Contains("outcome=completed", StringComparison.Ordinal)
            || notice.Contains("reason=actor_stopped", StringComparison.Ordinal))
            return false;
        var path = System.Text.RegularExpressions.Regex.Match(notice, @"result_file=(\S+)").Groups[1].Value;
        return File.Exists(path) && File.ReadAllText(path).Contains(ChildReplyText, StringComparison.Ordinal);
    }

    private protected sealed class ChildClients : IChatClientProvider
    {
        public ChildClients(FakeChatClient parent) => Parent = parent;

        public FakeChatClient Parent { get; }

        public FakeChatClient Child { get; } = new();

        // Sidecar roles (compaction, title, memory) use this client, so they never touch the parent or the child.
        public FakeChatClient Sidecar { get; } = new();

        public IChatClient GetClient(ModelRole role) => role switch
        {
            ModelRole.Main => Parent,
            ModelRole.Fallback => Child,
            _ => Sidecar
        };
    }

    private sealed class PlainSnapshotProvider : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context, TrustAudience audience, CancellationToken cancellationToken)
            => Task.FromResult(new WorkingContextSnapshot
            {
                WorkingContext = context,
                Git = new GitWorkingContextInspection.Skipped()
            });
    }

    private protected sealed class CountingTool(string name) : INetclawTool
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Name { get; } = name;

        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);

        public string Description => "Count the runs of an approved tool.";

        public string GrantCategory => "builtin";

        public JsonElement ParameterSchema => default;

        public AITool ToAITool() => AIFunctionFactory.Create(() => "confirmed", Name, Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult("confirmed");
        }
    }
}
