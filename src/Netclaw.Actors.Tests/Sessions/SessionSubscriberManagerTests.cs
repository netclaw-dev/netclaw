// -----------------------------------------------------------------------
// <copyright file="SessionSubscriberManagerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionSubscriberManagerTests : TestKit
{
    public SessionSubscriberManagerTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public void Same_subscriber_and_filter_is_rejoin()
    {
        var manager = new SessionSubscriberManager();
        var subscriber = CreateTestProbe("subscriber");

        manager.AddOrUpdate(subscriber.Ref, OutputFilter.TextOnly);

        Assert.True(manager.IsReJoin(subscriber.Ref, OutputFilter.TextOnly));
        Assert.False(manager.IsReJoin(subscriber.Ref, OutputFilter.Full));
    }

    [Fact]
    public async Task Emit_respects_content_filters_but_always_delivers_lifecycle()
    {
        var manager = new SessionSubscriberManager();
        var textOnly = CreateTestProbe("text-only");
        var usageOnly = CreateTestProbe("usage-only");
        var sessionId = new SessionId("channel/thread");

        manager.AddOrUpdate(textOnly.Ref, OutputFilter.TextOnly);
        manager.AddOrUpdate(usageOnly.Ref, OutputFilter.Usage);

        manager.Emit(new TextOutput("hello") { SessionId = sessionId }, OutputFilter.Text);

        await textOnly.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await usageOnly.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        manager.Emit(new TurnCompleted
        {
            SessionId = sessionId,
            TurnNumber = new TurnNumber(1)
        });

        await textOnly.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        await usageOnly.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Snapshot_preserves_async_callback_recipients_after_later_updates()
    {
        var manager = new SessionSubscriberManager();
        var original = CreateTestProbe("original");
        var replacement = CreateTestProbe("replacement");
        var sessionId = new SessionId("channel/thread");

        manager.AddOrUpdate(original.Ref, OutputFilter.ToolCalls);
        var snapshot = manager.Snapshot();

        manager.Remove(original.Ref);
        manager.AddOrUpdate(replacement.Ref, OutputFilter.ToolCalls);

        SessionSubscriberManager.Emit(snapshot, new ToolResultOutput
        {
            SessionId = sessionId,
            CallId = new ToolCallId("call-1"),
            ToolName = new ToolName("tool"),
            Result = "ok"
        }, OutputFilter.ToolCalls);

        await original.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await replacement.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }
}
