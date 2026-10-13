// -----------------------------------------------------------------------
// <copyright file="SessionExecutionLifetimeStopIdentityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionExecutionLifetimeStopIdentityTests
{
    [Fact]
    public void Stale_model_stop_reply_cannot_settle_a_replacement_call()
    {
        // Arrange: Finish one stop, then request a stop for the next call.
        var owner = new SessionExecutionLifetime(
            new ProcessingWatchdogTests.RecordingTimerScheduler(), ActorRefs.Nobody);
        var oldCall = owner.BeginModel();
        Assert.True(owner.RequestModelStop(oldCall.CallId));
        var oldReply = new ModelStoppedForRestart(oldCall.CallId, null);
        Assert.True(owner.AcceptModelStop(oldReply));

        var nextCall = owner.BeginModel();
        Assert.True(owner.RequestModelStop(nextCall.CallId));

        // Act: Deliver the old stop reply during the next stop.
        var staleAccepted = owner.AcceptModelStop(oldReply);

        // Assert: The old reply cannot release the current call.
        Assert.False(staleAccepted);
        Assert.True(owner.HasLiveModel);
        Assert.True(owner.AcceptModelStop(new ModelStoppedForRestart(nextCall.CallId, null)));
        Assert.False(owner.HasLiveModel);
    }
}
