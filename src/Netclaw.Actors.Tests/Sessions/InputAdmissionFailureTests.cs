// -----------------------------------------------------------------------
// <copyright file="InputAdmissionFailureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class InputAdmissionFailureTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly FakeChatClient _chatClient = new();

    protected override bool UseFaultableJournal => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Journal_write_problem_rejects_input_before_a_model_call(bool reject)
    {
        var journal = TestJournal.FromRef(AkkaPersistence.Instance.Apply(Sys).JournalFor(string.Empty));
        if (reject)
            await journal.OnWrite.RejectOnType<InputAdmitted>();
        else
            await journal.OnWrite.FailOnType<InputAdmitted>();

        var sessionId = new SessionId($"admission/journal-failure-{reject}");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("journal-failure-sub");
        await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var response = await manager.Ask<object>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Do not start this task"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var nack = Assert.IsType<CommandNack>(response);
        Assert.Contains("journal", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _chatClient.CallCount);
    }
}
