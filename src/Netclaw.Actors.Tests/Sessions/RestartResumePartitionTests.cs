// -----------------------------------------------------------------------
// <copyright file="RestartResumePartitionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Review p3. One resumed turn serves one requester. The restart resume takes the leading pending inputs of the
/// first requester. It stops at the first input of another requester, because the inputs that follow must keep the
/// admission order. A pending list that returns to the first requester after another one is the case that tells
/// "stop" from "skip".
/// </summary>
public sealed class RestartResumePartitionTests
{
    private static readonly SessionId Session = new("test/resume-partition");

    /// <summary>
    /// Pending <c>[A1, B1, A2]</c> resumes A1 alone. B1 and A2 wait, and each runs in its own turn, in order.
    /// Planted bug: the loop skips an input of another requester and counts the next input of the first requester.
    /// Then the count is 2, and the resume of the first two inputs mixes A1 and B1 in one turn.
    /// </summary>
    [Fact]
    public void The_resume_stops_at_the_first_input_of_another_requester()
    {
        // Arrange
        var pending = new[] { Input("a1", "alice"), Input("b1", "bob"), Input("a2", "alice") };

        // Act
        var result = CreateOwner().ValidateRestartResume(pending);

        // Assert
        Assert.Equal(1, result.ResumeCount);
        Assert.Equal("alice", result.Context?.RequesterSenderId?.Value);
    }

    /// <summary>The leading inputs of one requester resume together, up to the first input of another requester.</summary>
    [Fact]
    public void The_resume_takes_every_leading_input_of_the_first_requester()
    {
        // Arrange
        var pending = new[] { Input("a1", "alice"), Input("a2", "alice"), Input("b1", "bob"), Input("a3", "alice") };

        // Act
        var result = CreateOwner().ValidateRestartResume(pending);

        // Assert
        Assert.Equal(2, result.ResumeCount);
    }

    private static SessionExecutionLifetime CreateOwner()
        => new(new ProcessingWatchdogTests.RecordingTimerScheduler(), ActorRefs.Nobody);

    private static InputAdmitted Input(string id, string sender) => new()
    {
        SessionId = Session,
        InputId = new InputId(id),
        UserMessage = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = id },
        TurnContext = new TurnContextRecord
        {
            SessionId = Session,
            TurnId = $"turn-{id}",
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ChannelType = "signalr",
            RequesterSenderId = new SenderId(sender),
            RequesterPrincipal = PrincipalClassification.Operator
        },
        AdmittedAtMs = 1
    };
}
