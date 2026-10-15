// -----------------------------------------------------------------------
// <copyright file="AdoptedAuthorityTrustTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: a follow-up turn that serves another requester gets that requester's trust. The model call
/// carries the tools and the system prompt audience of the new requester, not those of the turn before.
/// <para>
/// The integration case in <c>LlmSessionIntegrationTests</c> checks only the message text of each batch.
/// This spec checks the authority that each model call used.
/// </para>
/// </summary>
public sealed class AdoptedAuthorityTrustTests : LlmSessionTestBase
{
    private const string ShellTool = "shell_execute";
    private const string SearchTool = "search_tools";

    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    public AdoptedAuthorityTrustTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 0,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new AudienceNamingPromptProvider());
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);

        // The shell tool is visible to the Personal audience only. The search tool is visible to every audience.
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create((string command) => command, ShellTool), "shell");
        registry.RegisterCore(AIFunctionFactory.Create(() => "found", SearchTool), "builtin");
        services.AddSingleton(registry);
        services.AddSingleton(TestToolAccessPolicy.Create(new ToolConfig()));
        services.AddSingleton(new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false)));
    }

    [Fact]
    public async Task A_follow_up_turn_for_another_requester_uses_that_requesters_tools_and_prompt()
    {
        // Arrange: a Personal turn runs. A Public input and a Personal input wait behind it.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.NextResponseGate = gate;
        var sessionId = new SessionId("channel-C/adopted-authority-trust");
        var subscriber = CreateTestProbe("adopted-authority-subscriber");
        await SessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        try
        {
            await SendAsync(sessionId, "Personal first", TrustAudience.Personal);
            await _fakeChatClient.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await SendAsync(sessionId, "Public second", TrustAudience.Public);
            await SendAsync(sessionId, "Personal third", TrustAudience.Personal);
            gate.TrySetResult();
            for (var turn = 0; turn < 3; turn++)
            {
                await subscriber.FishForMessageAsync(
                    message => message is TurnCompleted { Outcome: TurnOutcome.Completed },
                    TimeSpan.FromSeconds(10),
                    cancellationToken: TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            gate.TrySetResult();
        }

        // Assert: each call carries the prompt audience and the tool set of its own requester.
        Assert.Equal(3, _fakeChatClient.CallCount);
        AssertCall(0, "Personal first", "prompt-audience:Personal", shellVisible: true);
        AssertCall(1, "Public second", "prompt-audience:Public", shellVisible: false);
        AssertCall(2, "Personal third", "prompt-audience:Personal", shellVisible: true);
    }

    private void AssertCall(int index, string lastUserText, string promptMarker, bool shellVisible)
    {
        var request = _fakeChatClient.ReceivedMessages[index];
        Assert.Equal(lastUserText, request.Last(message => message.Role == Microsoft.Extensions.AI.ChatRole.User).Text);
        Assert.Contains(promptMarker, request.First(message => message.Role == Microsoft.Extensions.AI.ChatRole.System).Text);
        var tools = _fakeChatClient.ReceivedToolNames[index];
        Assert.Contains(SearchTool, tools);
        Assert.Equal(shellVisible, tools.Contains(ShellTool));
    }

    private IActorRef SessionManager => ActorRegistry.Get<SessionManagerActorKey>();

    private async Task SendAsync(SessionId sessionId, string content, TrustAudience audience)
        => await SessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = new MessageSource
            {
                ChannelType = audience == TrustAudience.Public ? ChannelType.Webhook : ChannelType.SignalR,
                SenderId = new SenderId(audience == TrustAudience.Public ? "webhook:test" : "operator-1"),
                ChannelId = audience == TrustAudience.Public ? "webhook-channel" : "operator-channel",
                MessageId = content.Replace(' ', '-'),
                TurnId = new TurnId("turn-" + content.Replace(' ', '-')),
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                Principal = audience == TrustAudience.Public
                    ? PrincipalClassification.VerifiedAutomation
                    : PrincipalClassification.Operator,
                Provenance = new SourceProvenance(
                    TransportAuthenticity.Verified,
                    audience == TrustAudience.Public ? PayloadTaint.Public : PayloadTaint.Trusted),
                ReceivedAt = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
            }
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private sealed class AudienceNamingPromptProvider : ISystemPromptProvider
    {
        public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null)
            => $"You are a test assistant. prompt-audience:{audience}";

        public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory) => null;

        public string? GetOperatingRules(TrustAudience audience) => null;
    }
}
