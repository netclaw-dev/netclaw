// -----------------------------------------------------------------------
// <copyright file="IdentityStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using ModelChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class IdentityStepViewModelTests : WizardStepTestBase
{

    [Fact]
    public void SubStepCount_IsFour()
    {
        using var step = new IdentityStepViewModel();
        Assert.Equal(4, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps()
    {
        using var step = new IdentityStepViewModel();

        for (var i = 0; i < step.SubStepCount - 1; i++)
        {
            Assert.True(step.TryAdvance());
            Assert.Equal(i + 1, step.CurrentSubStep);
        }

        // Last sub-step → complete
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryGoBack_ThroughSubSteps()
    {
        using var step = new IdentityStepViewModel();
        step.TryAdvance(); // → 1
        step.TryAdvance(); // → 2
        step.TryAdvance(); // → 3

        Assert.True(step.TryGoBack()); // 3 → 2
        Assert.Equal(2, step.CurrentSubStep);

        Assert.True(step.TryGoBack()); // 2 → 1
        Assert.True(step.TryGoBack()); // 1 → 0
        Assert.False(step.TryGoBack()); // at start
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new IdentityStepViewModel();
        var last = step.SubStepCount - 1;
        for (var i = 0; i < last; i++)
            step.TryAdvance();

        step.OnEnter(Context, NavigationDirection.Back);
        Assert.Equal(last, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_SetsIdentitySection()
    {
        using var step = new IdentityStepViewModel();
        step.AgentName = "TestBot";
        step.CommunicationStyle = "Detailed & formal";
        step.UserName = "Alice";
        step.UserTimezone = "America/New_York";

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Identity);
        Assert.Equal("TestBot", builder.Identity!.AgentName);
        Assert.Equal("Detailed & formal", builder.Identity.CommunicationStyle);
        Assert.Equal("Alice", builder.Identity.UserName);
        Assert.Equal("America/New_York", builder.Identity.UserTimezone);

        // Workspaces directory and notification webhooks are post-install settings
        // owned by `netclaw config`; the init Identity step must not contribute them.
        Assert.Null(builder.Workspaces);
        Assert.Null(builder.Notifications);
    }

    [Fact]
    public void WriteIdentityFiles_CreatesSoulAgentsAndTooling()
    {
        using var step = new IdentityStepViewModel();
        step.AgentName = "TestBot";
        step.CommunicationStyle = "Concise & casual";
        step.UserName = "Bob";
        step.UserTimezone = "UTC";

        step.WriteIdentityFiles(Context.Paths);

        Assert.True(File.Exists(Context.Paths.SoulPath));
        var soul = File.ReadAllText(Context.Paths.SoulPath);
        Assert.Contains("TestBot", soul);
        Assert.Contains("Bob", soul);
        Assert.Contains("UTC", soul);

        Assert.True(File.Exists(Context.Paths.AgentsPath));
        var agents = File.ReadAllText(Context.Paths.AgentsPath);
        Assert.Contains("Deployment Mission and Operating Playbook", agents);
        Assert.DoesNotContain("Search Decision Rules", agents);
        Assert.True(File.Exists(Context.Paths.ToolingPath));
    }

    [Fact]
    public void WriteIdentityFiles_PreservesExistingAgentsPlaybook()
    {
        using var step = new IdentityStepViewModel();
        const string existing = "# Mission\nNever skip the customer email review.";
        File.WriteAllText(Context.Paths.AgentsPath, existing);

        step.WriteIdentityFiles(Context.Paths);

        Assert.Equal(existing, File.ReadAllText(Context.Paths.AgentsPath));
    }

    [Fact]
    public void SeedBuiltInAgents_LoadsCanonicalWorkerAndExistingSpecialists()
    {
        using var step = new IdentityStepViewModel();

        step.SeedBuiltInAgents(Context.Paths);

        using var asset = typeof(IdentityStepViewModel).Assembly.GetManifestResourceStream(
            "Netclaw.Cli.Resources.identity.task-worker.profile.md");
        Assert.NotNull(asset);
        using var reader = new StreamReader(asset);
        Assert.Equal(reader.ReadToEnd(), File.ReadAllText(Path.Combine(Context.Paths.AgentsDirectory, "task-worker.md")));

        var profiles = new FileSubAgentDefinitionLoader(Context.Paths,
            NullLogger<FileSubAgentDefinitionLoader>.Instance).LoadAll();
        Assert.Equal(4, profiles.Count);
        var worker = Assert.Single(profiles, profile => profile.Name == "task-worker");
        Assert.Equal(ModelRole.Main, worker.ModelRole);
        Assert.Equal(120, worker.TimeoutSeconds);
        Assert.Null(worker.PrefillTimeoutSeconds);
        Assert.Equal(SubAgentVisibility.UserFacing, worker.Visibility);
        Assert.Empty(worker.ToolNames);
        Assert.Contains("complete requested artifact", worker.SystemPrompt);
        Assert.Contains("confirmed effects", worker.SystemPrompt);

        foreach (var (name, timeout) in new[] { ("research-assistant", 120), ("code-analyst", 120), ("summarizer", 60) })
        {
            var specialist = Assert.Single(profiles, profile => profile.Name == name);
            Assert.Equal(ModelRole.Compaction, specialist.ModelRole);
            Assert.Equal(timeout, specialist.TimeoutSeconds);
            Assert.Equal(SubAgentVisibility.UserFacing, specialist.Visibility);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Seeded_worker_requests_the_Main_client_through_the_real_spawner(string lineEnding)
    {
        using var step = new IdentityStepViewModel();
        step.SeedBuiltInAgents(Context.Paths);
        var workerPath = Path.Combine(Context.Paths.AgentsDirectory, "task-worker.md");
        File.WriteAllText(workerPath, File.ReadAllText(workerPath).ReplaceLineEndings(lineEnding));
        var profiles = new FileSubAgentDefinitionLoader(Context.Paths,
            NullLogger<FileSubAgentDefinitionLoader>.Instance).LoadAll();
        var worker = Assert.Single(profiles, profile => profile.Name == "task-worker");
        Assert.EndsWith(lineEnding, worker.SystemPrompt, StringComparison.Ordinal);
        var client = new FakeChatClient { ResponseText = "The scripted worker result." };
        var provider = new RoleRecordingClientProvider(new SingleClientProvider(client));
        var toolConfig = new ToolConfig();
        var pathPolicy = new ToolPathPolicy([]);
        var registry = new ToolRegistry();
        registry.Register(new FileReadTool(toolConfig, Context.Paths, pathPolicy));
        var policy = new ToolAccessPolicy(Context.Paths, toolConfig,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), pathPolicy);
        var snapshots = new WorkingContextSnapshotProvider(new GitWorkingContextInspector(TimeProvider.System),
            NullLogger<WorkingContextSnapshotProvider>.Instance);
        var spawner = new SubAgentSpawner(provider, registry, policy,
            approvalService: null, NullSystemPromptProvider.Instance, snapshots,
            NullLogger<SubAgentSpawner>.Instance);
        const string task = "Produce the complete plan for the assigned source revision.";
        const string runtimeContext = "Inspect the source revision named neutral-revision.";
        const string startCallId = "seeded-worker-start";
        var parent = new FakeChatClient
        {
            ResponseText = "The parent received the worker result.",
            ToolCallsOnFirstCall = [new FunctionCallContent(startCallId, "spawn_agent", new Dictionary<string, object?>
            {
                ["agent"] = worker.Name, ["task"] = task, ["context"] = runtimeContext,
                ["_rationale"] = "Delegate the complete source revision plan."
            })]
        };
        var definitions = new SubAgentDefinitionRegistry();
        definitions.Register(worker);
        registry.RegisterCore(new SpawnAgentTool(definitions, spawner, Context.Paths));
        var sessionId = new SessionId("signalr/seeded-worker");
        var sessionServices = new SessionServices(new SingleClientProvider(parent), NullSystemPromptProvider.Instance,
            [], snapshots, TimeProvider.System, Context.Paths, new TestSessionStorageResolver(Context.Paths));
        var tools = new SessionToolServices(new DispatchingToolExecutor(registry, policy), registry, policy,
            TrustDeriver: null, SkillRegistry: null, SubAgentRegistry: definitions, SubAgentSpawner: spawner);
        using var host = new HostBuilder().ConfigureServices(services =>
            services.AddAkka($"seeded-worker-{Guid.NewGuid():N}", (akka, _) =>
                akka.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization()
                    .StartActors((system, actors, _) =>
                    {
                        var owner = system.ActorOf(Props.Create(() => new LlmSessionActor(sessionId.Value,
                            new ModelCapabilities { ModelId = "scripted-parent", ContextWindowTokens = 128000 },
                            new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0, SnapshotInterval = 1000 } },
                            sessionServices, tools, null, null)), "seeded-worker-owner");
                        actors.Register<SessionManagerActorKey>(owner);
                    }))).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var owner = ActorRegistry.For(system).Get<SessionManagerActorKey>();
            var outputs = Channel.CreateUnbounded<SessionOutput>();
            var subscriber = system.ActorOf(Props.Create(() => new SeededWorkerOutputCollector(outputs.Writer)));
            var ceiling = TimeSpan.FromSeconds(30);
            await owner.Ask<SessionJoined>(new JoinSession(subscriber)
            { SessionId = sessionId, Filter = OutputFilter.Full }, ceiling, TestContext.Current.CancellationToken);
            await owner.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId, Content = task, Source = new MessageSource
                {
                    ChannelType = ChannelType.SignalR, SenderId = new SenderId("worker-test-operator"),
                    Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
                    Principal = PrincipalClassification.Operator,
                    Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
                }
            }, ceiling, TestContext.Current.CancellationToken);
            var observed = new List<SessionOutput>();
            while (true)
            {
                var output = await outputs.Reader.ReadAsync(TestContext.Current.CancellationToken)
                    .AsTask().WaitAsync(ceiling, TestContext.Current.CancellationToken);
                observed.Add(output);
                if (output is TurnCompleted
                    && observed.OfType<SubAgentOutput>().Any(child => child.Phase == SubAgentPhase.Completed)
                    && parent.ReceivedMessagesByCall.Any(messages => messages
                        .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                        .Any(result => result.CallId.StartsWith("child-result-", StringComparison.Ordinal))))
                    break;
            }
            Assert.Empty(observed.OfType<ErrorOutput>());
            var completion = Assert.Single(observed.OfType<SubAgentOutput>(), output => output.Phase == SubAgentPhase.Completed);
            Assert.Equal(new AgentName("task-worker"), completion.AgentName);
            Assert.True(completion.Success);
            Assert.Equal(SubAgentRunOutcome.Completed, completion.Outcome);
            Assert.Equal([ModelRole.Main], provider.RequestedRoles);
            Assert.Equal(1, client.CallCount);
            var request = Assert.Single(client.ReceivedMessagesByCall);
            // The actor trims the profile end before it adds the tool index.
            Assert.Contains(request, message => message.Role == ModelChatRole.System
                && message.Text.Contains(worker.SystemPrompt.TrimEnd(), StringComparison.Ordinal));
            Assert.Contains(request, message => message.Role == ModelChatRole.User
                && message.Text.StartsWith($"Context:\n{runtimeContext}\n\n", StringComparison.Ordinal)
                && message.Text.EndsWith($"\nTask:\n{task}", StringComparison.Ordinal));
            var results = parent.ReceivedMessagesByCall[^1]
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
            var accepted = Assert.Single(results, result => result.CallId == startCallId);
            using var acceptance = JsonDocument.Parse(Assert.IsType<string>(accepted.Result));
            Assert.Equal("Accepted", acceptance.RootElement.GetProperty("state").GetString());
            Assert.Equal("check_agent_run", acceptance.RootElement.GetProperty("control_tool").GetString());
            var delivered = Assert.Single(results, result => result.CallId.StartsWith("child-result-", StringComparison.Ordinal));
            using var terminal = JsonDocument.Parse(Assert.IsType<string>(delivered.Result));
            Assert.Equal(acceptance.RootElement.GetProperty("run_id").GetString(), terminal.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(acceptance.RootElement.GetProperty("scope_id").GetString(), terminal.RootElement.GetProperty("scope_id").GetString());
            Assert.Equal("Completed", terminal.RootElement.GetProperty("state").GetString());
            Assert.Equal("Completed", terminal.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("The scripted worker result.", terminal.RootElement.GetProperty("output").GetString());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void SeedBuiltInAgents_PreservesCustomWorkerAndOtherOperatorFiles()
    {
        using var step = new IdentityStepViewModel();
        const string custom = """
            ---
            name: task-worker
            description: Operator custom worker
            modelRole: Compaction
            timeoutSeconds: 90
            visibility: user-facing
            ---
            Preserve the operator's task mission.
            """;
        var workerPath = Path.Combine(Context.Paths.AgentsDirectory, "task-worker.md");
        File.WriteAllText(workerPath, custom);
        File.WriteAllText(Context.Paths.AgentsPath, "Operator mission playbook.");
        File.WriteAllText(Context.Paths.SoulPath, "Operator identity.");
        File.WriteAllText(Context.Paths.ToolingPath, "Operator tools.");

        step.SeedBuiltInAgents(Context.Paths);
        var firstSeed = Directory.EnumerateFiles(Context.Paths.AgentsDirectory)
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
        step.SeedBuiltInAgents(Context.Paths);

        foreach (var (name, content) in firstSeed)
            Assert.Equal(content, File.ReadAllText(Path.Combine(Context.Paths.AgentsDirectory, name)));
        Assert.Equal(custom, File.ReadAllText(workerPath));
        Assert.Equal("Operator mission playbook.", File.ReadAllText(Context.Paths.AgentsPath));
        Assert.Equal("Operator identity.", File.ReadAllText(Context.Paths.SoulPath));
        Assert.Equal("Operator tools.", File.ReadAllText(Context.Paths.ToolingPath));
        var profiles = new FileSubAgentDefinitionLoader(Context.Paths,
            NullLogger<FileSubAgentDefinitionLoader>.Instance).LoadAll();
        var worker = Assert.Single(profiles, profile => profile.Name == "task-worker");
        Assert.Equal(ModelRole.Compaction, worker.ModelRole);
        Assert.Equal(90, worker.TimeoutSeconds);
        Assert.Contains("operator's task mission", worker.SystemPrompt);
    }

    [Fact]
    public void BuildOnboardingTrigger_SeparatesSoulFromMissionAndRequiresConfirmation()
    {
        using var step = new IdentityStepViewModel();

        var trigger = step.BuildOnboardingTrigger(Context.Paths);

        Assert.Contains(Context.Paths.SoulPath, trigger);
        Assert.Contains(Context.Paths.AgentsPath, trigger);
        Assert.Contains("skill-selection rules", trigger);
        Assert.Contains("ask me to confirm", trigger);
        Assert.Contains("next message", trigger);
    }

    [Fact]
    public void DefaultValues()
    {
        using var step = new IdentityStepViewModel();
        Assert.Equal("Netclaw", step.AgentName);
        Assert.Null(step.CommunicationStyle);
        Assert.Equal(TimeZoneInfo.Local.Id, step.UserTimezone);
    }

    [Fact]
    public void OnEnter_PrefillsFromExistingConfig()
    {
        using var step = new IdentityStepViewModel();
        using var context = new WizardContext
        {
            Paths = Context.Paths,
            Registry = Context.Registry,
            RequestRedraw = () => { },
            ExistingConfig = new Dictionary<string, object>
            {
                ["Identity"] = new Dictionary<string, object>
                {
                    ["AgentName"] = "ExistingBot",
                    ["CommunicationStyle"] = "Detailed & casual",
                    ["UserName"] = "Dana",
                    ["UserTimezone"] = "UTC"
                }
            }
        };

        step.OnEnter(context, NavigationDirection.Forward);

        Assert.Equal("ExistingBot", step.AgentName);
        Assert.Equal("Detailed & casual", step.CommunicationStyle);
        Assert.Equal("Dana", step.UserName);
        Assert.Equal("UTC", step.UserTimezone);
    }

    private sealed class SeededWorkerOutputCollector : ReceiveActor
    {
        public SeededWorkerOutputCollector(ChannelWriter<SessionOutput> outputs)
            => Receive<SessionOutput>(output => outputs.TryWrite(output));
    }

    private sealed class RoleRecordingClientProvider(IChatClientProvider inner) : IChatClientProvider
    {
        public List<ModelRole> RequestedRoles { get; } = [];

        public IChatClient GetClient(ModelRole role)
        {
            RequestedRoles.Add(role);
            return inner.GetClient(role);
        }
    }
}
