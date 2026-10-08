// -----------------------------------------------------------------------
// <copyright file="ReminderPreflightGrantTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Reminders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Jobs;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using FakeChatClient = Netclaw.Actors.Tests.Sessions.FakeChatClient;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// The scheduled run of a reminder is unattended, so it cannot prompt. These
/// tests prove that a person can test the reminder in a chat with
/// <c>run_reminder</c>, save an "Always in this folder" grant there, and that
/// the unattended scheduled run then reads that grant.
/// </summary>
[Collection(BackgroundJobProcessCollection.Name)]
public sealed class ReminderPreflightGrantTests : LlmSessionTestBase
{
    private const string ReminderName = "disk-cleanup-weekly";
    private const string Requester = "local-user";
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeChatClient _chatClient = new();
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $".netclaw-reminder-preflight-{Guid.NewGuid():N}");
    private NetclawPaths _paths = null!;
    private ShellExecutionEnvironment _environment = null!;
    private ToolApprovalStore _store = null!;
    private ToolRegistry _registry = null!;

    public ReminderPreflightGrantTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        _paths = new NetclawPaths(_root, Path.Combine(_root, "workspaces"));
        _paths.EnsureDirectoriesExist();
        _environment = TestShellEnvironment.Current;

        var config = new ToolConfig
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(DeploymentPosture.Personal)
        };
        var policy = new ToolAccessPolicy(
            _paths,
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(_environment),
            new ToolPathPolicy(_environment, []),
            safeVerbs: SafeVerbLoader.Load(_environment.Platform == ShellPlatform.Windows));
        _registry = new ToolRegistry();
        _registry.WithFirstPartyTools(policy);
        _store = new ToolApprovalStore(
            _paths.ToolApprovalsPath,
            TimeProvider.System,
            new ApprovalStoreMigrationContext(
                _environment.Grammar == ShellGrammar.Bash ? ApprovalShell.Bash : ApprovalShell.PowerShell),
            lockTimeout: TimeSpan.Zero);

        services.AddSingleton(_paths);
        services.AddSingleton(_environment);
        services.AddSingleton(config);
        services.AddSingleton(policy);
        services.AddSingleton(new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false)));
        services.AddSingleton(_registry);
        services.AddSingleton(_store);
        services.AddSingleton<IToolApprovalService, AkkaToolApprovalService>();
        services.AddSingleton<IToolExecutor>(provider => new DispatchingToolExecutor(
            _registry, policy, provider.GetRequiredService<IToolApprovalService>()));
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning { SnapshotInterval = 1, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant with tools."));
    }

    protected override void DeleteOwnedDirectories()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Folder_grant_saved_in_a_chat_test_lets_the_unattended_scheduled_run_pass()
    {
        var cache = CreateDirectory("cache");
        SaveReminder(TrustAudience.Personal, TrustBoundary.Personal);
        RegisterRunReminderTool();

        // The chat test: the model calls run_reminder, then runs the command
        // that the returned prompt asks for. The person answers the prompt.
        _chatClient.PlannedResponses.Enqueue([RunReminderCall()]);
        _chatClient.PlannedResponses.Enqueue([ShellCall("chat-shell", cache)]);
        _chatClient.PlannedResponses.Enqueue([new TextContent("Test done.")]);
        var chat = await StartChatAsync("signalr/reminder-preflight");

        var runResult = await chat.Subscriber.FishForMessageAsync<ToolResultOutput>(
            result => result.ToolName.Value == ToolAudienceProfileToolCatalog.RunReminder,
            TimeSpan.FromSeconds(20),
            cancellationToken: TestContext.Current.CancellationToken);
        var request = await chat.Subscriber.FishForMessageAsync<ToolInteractionRequest>(
            _ => true,
            TimeSpan.FromSeconds(20),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ShellTool.ToolName, request.ToolName.Value);
        Assert.Contains(request.Options, option => option.Key == ApprovalOptionKeys.ApproveAlwaysKey);

        var reply = await chat.Manager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = chat.SessionId,
            CallId = request.CallId,
            SelectedKey = ApprovalOptionKeys.ApproveAlwaysKey,
            SenderId = new SenderId(Requester)
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);
        await chat.Subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(20),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("x", ReadMarker(cache));
        var grant = Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, ShellTool.ToolName));
        Assert.True(PathUtility.AreEquivalentPaths(cache, grant.Directory!));

        // The scheduled run: unattended, a new session, the same command.
        _chatClient.PlannedResponses.Enqueue([ShellCall("scheduled-shell", cache)]);
        _chatClient.PlannedResponses.Enqueue([new TextContent("Cleanup done.")]);
        await FireReminderAsync();

        Assert.Equal("xx", ReadMarker(cache));
        // The chat test used the same prompt text as the scheduled run.
        Assert.Contains(ScheduledPrompt(), runResult.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_saved_grant_the_unattended_scheduled_run_denies_the_command()
    {
        var cache = CreateDirectory("cache");
        SaveReminder(TrustAudience.Personal, TrustBoundary.Personal);

        _chatClient.PlannedResponses.Enqueue([ShellCall("scheduled-shell", cache)]);
        _chatClient.PlannedResponses.Enqueue([new TextContent("Cleanup failed.")]);
        await FireReminderAsync();

        Assert.False(File.Exists(MarkerPath(cache)));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, ShellTool.ToolName));
    }

    [Fact]
    public async Task A_chat_with_a_wider_audience_than_the_reminder_is_refused()
    {
        SaveReminder(TrustAudience.Team, TrustBoundary.Team);
        var tool = new RunReminderTool(ActorRegistry.Get<ReminderManagerActorKey>(), new SchedulingConfig());
        var personalChat = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
        });

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Id"] = ReminderName },
            personalChat,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.Contains("Run this in a team chat", result, StringComparison.Ordinal);
        Assert.DoesNotContain(ScheduledPrompt(), result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unattended_caller_is_refused()
    {
        SaveReminder(TrustAudience.Personal, TrustBoundary.Personal);
        var tool = new RunReminderTool(ActorRegistry.Get<ReminderManagerActorKey>(), new SchedulingConfig());
        var unattended = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false)
        });

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Id"] = ReminderName },
            unattended,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.DoesNotContain(ScheduledPrompt(), result, StringComparison.Ordinal);
    }

    private string MarkerCommand => _environment.Grammar == ShellGrammar.PowerShell
        ? "Add-Content -NoNewline -Path launch-count.txt -Value x"
        : "printf x >> launch-count.txt";

    private string ScheduledPrompt() => $"Clean the cache folder with: {MarkerCommand}";

    private void SaveReminder(TrustAudience audience, TrustBoundary boundary)
    {
        var now = TimeProvider.System.GetUtcNow();
        Host.Services.GetRequiredService<ReminderDefinitionStore>().Save(new ReminderDefinition
        {
            Id = new ReminderId(ReminderName),
            Title = "Weekly disk cleanup",
            Instructions = ScheduledPrompt(),
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.Interval, Interval = TimeSpan.FromDays(7) },
            Audience = audience,
            Boundary = boundary,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    // Production registers reminder tools after the actor system starts,
    // because the tools need the reminder manager. This test does the same.
    private void RegisterRunReminderTool()
        => _registry.Register(new RunReminderTool(ActorRegistry.Get<ReminderManagerActorKey>(), new SchedulingConfig()));

    private static FunctionCallContent RunReminderCall()
        => new("chat-run", ToolAudienceProfileToolCatalog.RunReminder, ToolInput.Create("Id", ReminderName, "_rationale", "Test the reminder."));

    private FunctionCallContent ShellCall(string callId, string workingDirectory)
        => new(callId, ShellTool.ToolName, ToolInput.Create(
            "Command", MarkerCommand,
            "WorkingDirectory", workingDirectory,
            "_rationale", "Clean the cache folder."));

    private async Task<ChatJourney> StartChatAsync(string sessionValue)
    {
        var sessionId = new SessionId(sessionValue);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = ReminderName,
            Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR,
                SenderId = new SenderId(Requester),
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
                ReceivedAt = ReceivedAt,
            }
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return new ChatJourney(sessionId, manager, subscriber);
    }

    /// <summary>Fires the reminder the way Akka.Reminders does and waits for its history record.</summary>
    private async Task FireReminderAsync()
    {
        var id = new ReminderId(ReminderName);
        ActorRegistry.Get<ReminderManagerActorKey>().Tell(new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(ReminderName),
            TimeProvider.System.GetUtcNow(),
            ReminderDeadline.Infinite,
            new ReminderPayload { Id = id }));

        var history = Host.Services.GetRequiredService<ReminderHistoryStore>();
        await AwaitAssertAsync(async () =>
        {
            var record = Assert.Single(await history.ReadAsync(id, 10));
            Assert.True(record.Success, record.ErrorMessage);
        }, TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
    }

    private string CreateDirectory(string name)
        => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string MarkerPath(string directory) => Path.Combine(directory, "launch-count.txt");

    private static string ReadMarker(string directory) => File.ReadAllText(MarkerPath(directory));

    private sealed record ChatJourney(SessionId SessionId, IActorRef Manager, Akka.TestKit.TestProbe Subscriber);
}
