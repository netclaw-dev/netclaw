// -----------------------------------------------------------------------
// <copyright file="SlashCommandSkillContentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// An inline slash command puts the skill body into the first model call of the
/// turn. The working-context snapshot is built asynchronously, so the call is
/// assembled after the command handler has returned.
/// </summary>
public sealed class SlashCommandSkillContentTests : LlmSessionTestBase
{
    private const string SkillBodyMarker = "SKILL-BODY-MARKER-7f3a";

    private readonly FakeChatClient _chatClient = new();
    private readonly GatedSnapshotProvider _snapshots = new();

    public SlashCommandSkillContentTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IWorkingContextSnapshotProvider>(_snapshots);
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        services.AddSingleton(new ToolRegistry());

        var skillDir = Path.Combine(TestPaths.BasePath, "slash-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile, $"""
            ---
            name: demo-skill
            description: Demo skill.
            ---

            # Demo

            {SkillBodyMarker}
            """);
        var skillRegistry = new SkillRegistry();
        skillRegistry.Register(new SkillEntry("demo-skill", "Demo", "Demo skill.", skillFile, skillDir, null));
        services.AddSingleton(skillRegistry);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task First_model_call_after_an_inline_slash_command_carries_the_skill_body(bool delaySnapshot, bool failSnapshot)
    {
        _snapshots.Fail = failSnapshot;
        if (!delaySnapshot)
            _snapshots.Release();

        var sessionId = new SessionId($"test-channel/slash-body-{delaySnapshot}-{failSnapshot}");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("slash-body-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/demo-skill run it"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await _snapshots.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (delaySnapshot)
            Assert.Equal(0, _chatClient.CallCount);
        _snapshots.Release();

        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var firstCall = _chatClient.ReceivedMessages[0];
        Assert.Contains(firstCall, message => message.Text.Contains(SkillBodyMarker, StringComparison.Ordinal));
    }

    private sealed class GatedSnapshotProvider : IWorkingContextSnapshotProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fail { get; set; }

        public void Release() => _gate.TrySetResult();

        public async Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            if (Fail)
                throw new InvalidOperationException("snapshot failed");
            return new WorkingContextSnapshot
            {
                WorkingContext = context,
                Git = new GitWorkingContextInspection.Unavailable("test")
            };
        }
    }
}
