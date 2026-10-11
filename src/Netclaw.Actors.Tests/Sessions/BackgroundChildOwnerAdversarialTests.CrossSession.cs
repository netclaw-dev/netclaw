// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.CrossSession.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_session_cannot_read_or_cancel_a_child_with_the_same_requester(bool cancel)
    {
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Delegate the neutral task.",
            Source = DeliverySource("original", "operator-a")
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var original = await ReadJournalAsync();
        var accepted = Assert.Single(original.OfType<ChildRunAccepted>());
        var storage = Assert.IsType<ToolSessionScope.Bound>(_start!.Prepared!.Execution.Scope.Authority.Session).Storage;
        var foreignSession = new SessionId("signalr/other-owner-adversarial");
        var foreignSubscriber = CreateTestProbe();
        manager.Tell(new JoinSession(foreignSubscriber)
        { SessionId = foreignSession, Filter = OutputFilter.Full }, foreignSubscriber.Ref);
        await foreignSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        var foreignOwner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(foreignSession.Value)}")
            .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
        Assert.NotEqual(owner, foreignOwner);
        _main.PlannedResponses.Enqueue([StatusCall("cross-session-control", cancel)]);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = foreignSession, Content = "Inspect the recorded child.",
            Source = DeliverySource("foreign-session", "operator-a")
        }, Ceiling, TestContext.Current.CancellationToken);
        var denied = Assert.IsType<ToolResultOutput>(await foreignSubscriber.FishForMessageAsync<object>(
            message => message is ToolResultOutput, Ceiling, cancellationToken: TestContext.Current.CancellationToken));
        await CompletedAsync(foreignSubscriber);
        Assert.Equal("Error: the child run was not found or is not accessible from this session.", denied.Result);
        foreach (var detail in new[] { accepted.Run.RunId.Value, accepted.Run.ScopeId.Value,
                     storage.LogPath.Value, storage.ArtifactDirectory.Value, "log_path", "artifact_directory" })
            Assert.DoesNotContain(detail, denied.Result, StringComparison.Ordinal);
        var deniedPair = Assert.Single(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
        Assert.Equal("cross-session-control", deniedPair.CallId);
        Assert.Equal(denied.Result, Assert.IsType<string>(deniedPair.Result));
        var retained = await ReadJournalAsync();
        Assert.Equal(original.Length, retained.Length);
        Assert.Empty(retained.OfType<ChildRunEvent.CancellationRequested>());
        Assert.Empty(retained.OfType<ChildRunEvent.DispatchClosed>());
        Assert.Empty(retained.OfType<ChildRunEvent.TerminalRecorded>());
        Assert.False(_childRelease.Task.IsCompleted);
        Assert.Equal(1, _child.CallCount);
        Assert.Equal(4, _main.CallCount);

        _main.PlannedResponses.Enqueue([StatusCall("original-session-control", false)]);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Inspect the child in its original session.",
            Source = DeliverySource("original-session-control", "operator-a")
        }, Ceiling, TestContext.Current.CancellationToken);
        var allowed = Assert.IsType<ToolResultOutput>(await subscriber.FishForMessageAsync<object>(
            message => message is ToolResultOutput, Ceiling, cancellationToken: TestContext.Current.CancellationToken));
        await CompletedAsync(subscriber);
        using var body = JsonDocument.Parse(allowed.Result);
        Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
        Assert.Equal(accepted.Run.ScopeId.Value, body.RootElement.GetProperty("scope_id").GetString());
        Assert.Equal("Running", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(storage.LogPath.Value, body.RootElement.GetProperty("log_path").GetString());
        Assert.Equal(storage.ArtifactDirectory.Value, body.RootElement.GetProperty("artifact_directory").GetString());
        Assert.False(body.RootElement.GetProperty("cancellation_requested").GetBoolean());
        Assert.False(body.RootElement.GetProperty("dispatch_closed").GetBoolean());
        var result = Assert.Single((await ReadJournalAsync()).OfType<ToolCallRecorded>(),
            record => record.ToolResult.ToolCallId == new ToolCallId("original-session-control"));
        Assert.Equal(allowed.Result, result.ToolResult.Content);
        var currentPair = Assert.Single(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            pair => pair.CallId == "original-session-control");
        Assert.Equal(allowed.Result, Assert.IsType<string>(currentPair.Result));
        Assert.False(_childRelease.Task.IsCompleted);
        Assert.Equal(1, _child.CallCount);
        Assert.Equal(6, _main.CallCount);

        FunctionCallContent StatusCall(string id, bool requestCancel) => new(id, "status_json_probe",
            new Dictionary<string, object?>
            {
                ["run_id"] = accepted.Run.RunId.Value, ["cancel"] = requestCancel,
                ["_rationale"] = "Inspect the child through its session owner."
            });
    }
}
