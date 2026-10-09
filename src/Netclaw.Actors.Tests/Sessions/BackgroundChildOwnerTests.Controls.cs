// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerTests.Controls.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_later_foreign_requester_cannot_read_or_cancel_the_original_child(bool cancel)
    {
        var session = new SessionId("signalr/background-control");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        MessageSource Source(string turn, string sender) => new()
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId(sender), TurnId = new TurnId(turn),
            MessageId = turn, Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        };
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = "Start the control fixture.", Source = Source("original", "operator-a") },
            Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = _start!.Prepared!.RunId;
        async Task Control(string turn, string sender, bool requestCancel)
        {
            _main.PlannedResponses.Enqueue([new FunctionCallContent(turn, "control_probe", new Dictionary<string, object?>
            { ["_rationale"] = "Inspect the fixture run.", ["run_id"] = runId.Value, ["cancel"] = requestCancel })]);
            _main.PlannedResponses.Enqueue([new TextContent("The control request returned.")]);
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = session, Content = "Check the fixture run.", Source = Source(turn, sender) },
                Ceiling, TestContext.Current.CancellationToken);
            await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
        }
        await Control("foreign", "operator-b", cancel);
        Assert.NotNull(_start.LastControlReply);
        Assert.Null(_start.LastControlReply.Run);
        Assert.DoesNotContain("log_path", _start.LastControlBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("artifact_directory", _start.LastControlBody!, StringComparison.Ordinal);
        Assert.Equal(1, _child.CallCount);
        await Control("original-status", "operator-a", false);
        var retained = Assert.IsType<BackgroundChildRun>(_start.LastControlReply!.Run);
        Assert.Equal(BackgroundChildState.Running, retained.State);
        Assert.Null(retained.CancellationRequestedAtMs);
        Assert.Null(retained.DispatchClosedAtMs);
        Assert.Equal(1, _child.CallCount);
        using var status = JsonDocument.Parse(_start.LastControlBody!);
        var logPath = status.RootElement.GetProperty("log_path").GetString()!;
        var artifactDirectory = status.RootElement.GetProperty("artifact_directory").GetString()!;
        Assert.True(Path.IsPathFullyQualified(logPath));
        Assert.True(Path.IsPathFullyQualified(artifactDirectory));
        var childStorage = Assert.IsType<ToolSessionScope.Bound>(_start.Prepared!.Execution.Scope.Authority.Session).Storage;
        Assert.Equal(childStorage.LogPath.Value, logPath);
        Assert.Equal(childStorage.ArtifactDirectory.Value, artifactDirectory);
        Assert.True(File.Exists(logPath));
        _main.PlannedResponses.Enqueue([new FunctionCallContent("read-live-log", "live_log_read", new Dictionary<string, object?>
        { ["_rationale"] = "Read the authorized child log.", ["Path"] = logPath })]);
        _main.PlannedResponses.Enqueue([new TextContent("The live log read completed.")]);
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = session, Content = "Read the child log.", Source = Source("read-log", "operator-a") },
            Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<ToolInvocationReceipt.Succeeded>(_start.LastLogReceipt);
        Assert.NotNull(_start.LastLogRead);
        Assert.False(_childRelease.Task.IsCompleted);
        Assert.Equal(1, _child.CallCount);
        _childRelease.TrySetResult();
    }
}
