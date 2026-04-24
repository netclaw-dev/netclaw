using System.Collections.Concurrent;
using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

public sealed class RecordingSessionPipeline(
    Func<SessionId, IReadOnlyList<SessionOutput>> outputFactory) : ISessionPipeline
{
    public SessionPipelineOptions? CapturedOptions { get; private set; }
    public List<IWithSessionId> RecordedFeedback { get; } = [];
    public ConcurrentQueue<ChannelInput> CapturedInputs { get; } = new();

    public Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default)
    {
        CapturedOptions = options;

        var killSwitch = KillSwitches.Shared($"recording-{sessionId.Value}");

        var input = Sink.ForEach<ChannelInput>(ci => CapturedInputs.Enqueue(ci))
            .MapMaterializedValue<NotUsed>(_ => NotUsed.Instance);

        var output = Source.From(outputFactory(sessionId).ToList())
            .Concat(Source.Never<SessionOutput>())
            .Via(killSwitch.Flow<SessionOutput>());

        return Task.FromResult(new MaterializedSession(input, output, killSwitch));
    }

    public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
    {
        RecordedFeedback.Add(feedback);
        return Task.CompletedTask;
    }
}
