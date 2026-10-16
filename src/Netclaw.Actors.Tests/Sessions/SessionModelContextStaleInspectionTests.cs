// -----------------------------------------------------------------------
// <copyright file="SessionModelContextStaleInspectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class SessionModelContextTests
{
    private static readonly SessionId SessionId = new("context-owner/session-1");

    [Fact]
    public async Task A_later_inspection_rejects_an_old_reply_and_invalidation_rejects_the_current_reply()
    {
        // Arrange
        var owner = CreateOwner(new StaticSystemPromptProvider("stable prompt"));
        owner.SetOverlay("current context");
        var firstState = SessionState.Empty.AddUserMessage("first question");
        var firstCall = Assert.IsType<ContextCall.Inspect>(Start(owner, 41, firstState));
        var firstReply = Assert.IsType<WorkingContextSnapshotReady>(await firstCall.Work);
        owner.ResetForNewTurn();
        var secondState = SessionState.Empty.AddUserMessage("second question");
        var secondCall = Assert.IsType<ContextCall.Inspect>(Start(owner, 42, secondState));
        var secondReply = Assert.IsType<WorkingContextSnapshotReady>(await secondCall.Work);

        // Act
        var stale = owner.CompleteInspection(
            firstReply, firstState, TrustAudience.Personal, currentToolTrust: null);
        var current = owner.CompleteInspection(
            secondReply, secondState, TrustAudience.Personal, currentToolTrust: null);
        owner.ResetForNewTurn();
        var stoppedCall = Assert.IsType<ContextCall.Inspect>(Start(owner, 43, secondState));
        var stoppedReply = Assert.IsType<WorkingContextSnapshotReady>(await stoppedCall.Work);
        owner.InvalidateInspection();
        var stopped = owner.CompleteInspection(
            stoppedReply, secondState, TrustAudience.Personal, currentToolTrust: null);

        // Assert
        Assert.Null(stale);
        Assert.Equal(0, NudgeCount(firstState));
        Assert.NotNull(current);
        Assert.Equal(1, NudgeCount(current.State));
        Assert.Null(stopped);
        Assert.Equal(0, NudgeCount(secondState));
    }

    private static ContextCall Start(
        SessionModelContext owner,
        long callId,
        SessionState state,
        MessageSource? sourceFallback = null,
        bool forceNoTools = false)
        => owner.StartCall(
            callId,
            state,
           
            admittedTurn: null,
            sourceFallback,
            toolTrust: null,
            recallQuery: null,
            forceNoTools,
            skillBody: null,
            restartNotice: null,
            CancellationToken.None);

    private static int NudgeCount(SessionState state)
        => state.History.Count(SessionState.IsSystemNudge);

    private static SessionModelContext CreateOwner(
        ISystemPromptProvider prompt,
        IWorkingContextSnapshotProvider? snapshots = null,
        SessionMemoryServices? memory = null,
        DiscoveredToolCache? discoveredTools = null,
        SessionToolServices? tools = null)
    {
        var paths = new NetclawPaths(Path.Combine(AppContext.BaseDirectory, "model-context-tests"));
        var storage = TestSessionStorageResolver.Instance.Resolve(SessionId);
        var services = new SessionServices(
            new UnexpectedChatClientProvider(),
            prompt,
            [],
            snapshots ?? new ImmediateSnapshotProvider(),
            TimeProvider.System,
            paths,
            TestSessionStorageResolver.Instance);
        return new SessionModelContext(
            SessionId,
            storage,
            services,
            memory,
            tools,
            discoveredTools ?? new DiscoveredToolCache(),
            ModelModality.Text,
            (ILoggingAdapter)NoLogger.Instance);
    }

    private sealed class UnexpectedChatClientProvider : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role)
            => throw new InvalidOperationException("The context owner must not resolve a chat client.");
    }

    private sealed class ImmediateSnapshotProvider : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken)
            => Task.FromResult(new WorkingContextSnapshot
            {
                WorkingContext = context,
                Git = new GitWorkingContextInspection.NotRepository()
            });
    }
}
