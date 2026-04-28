// -----------------------------------------------------------------------
// <copyright file="ScriptedSessionPipeline.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

/// <summary>
/// Fake <see cref="ISessionPipeline"/> that replays a scripted sequence of
/// <see cref="SessionOutput"/> events. Input is discarded. Used by
/// execution actor tests (reminders, webhooks) and pipeline handle tests.
/// </summary>
internal sealed class ScriptedSessionPipeline(
    Func<SessionId, IReadOnlyList<SessionOutput>> outputFactory) : ISessionPipeline
{
    public SessionPipelineOptions? CapturedOptions { get; private set; }

    public Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default)
    {
        CapturedOptions = options;

        var killSwitch = KillSwitches.Shared($"scripted-{sessionId.Value}");

        var input = Sink.Ignore<ChannelInput>()
            .MapMaterializedValue<NotUsed>(_ => NotUsed.Instance);

        var output = Source.From(outputFactory(sessionId).ToList())
            .Via(killSwitch.Flow<SessionOutput>());

        return Task.FromResult(new MaterializedSession(input, output, killSwitch));
    }

    public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
        Task.CompletedTask;
}
