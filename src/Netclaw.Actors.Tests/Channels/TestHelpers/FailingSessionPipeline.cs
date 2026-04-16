using Akka.Streams;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

/// <summary>
/// Fake <see cref="ISessionPipeline"/> that throws a pre-configured exception
/// on <see cref="CreateAsync"/>. Used to test initialization failure paths.
/// </summary>
internal sealed class FailingSessionPipeline(Exception exception) : ISessionPipeline
{
    public Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default) =>
        throw exception;

    public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
        Task.CompletedTask;
}
