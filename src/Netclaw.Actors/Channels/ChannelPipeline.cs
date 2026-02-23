using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Options for creating a session pipeline.
/// </summary>
public sealed record SessionPipelineOptions
{
    /// <summary>
    /// Channel type identifier (e.g. "console", "headless", "slack").
    /// Used to populate <see cref="MessageSource.ChannelType"/> on inbound messages.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Which output categories the channel wants to receive.
    /// </summary>
    public OutputFilter Filter { get; init; } = OutputFilter.Full;
}

/// <summary>
/// Handle to a materialized session. Exposes typed Akka.Streams for
/// bidirectional communication with an LLM session actor — all actor
/// internals (JoinSession, subscriber refs, message routing) are hidden.
/// </summary>
public sealed class MaterializedSession : IAsyncDisposable
{
    private readonly SharedKillSwitch _killSwitch;

    internal MaterializedSession(
        Sink<ChannelInput, NotUsed> input,
        Source<SessionOutput, NotUsed> output,
        SharedKillSwitch killSwitch)
    {
        Input = input;
        Output = output;
        _killSwitch = killSwitch;
    }

    /// <summary>
    /// Input sink. Encapsulates <see cref="ChannelInput"/> →
    /// <see cref="SendUserMessage"/> transformation and delivery to the
    /// session manager. Channel connects its own Source:
    /// <code>
    /// Source.Queue&lt;ChannelInput&gt;(16, Backpressure)
    ///     .ToMat(session.Input, Keep.Left)
    ///     .Run(system);
    /// </code>
    /// </summary>
    public Sink<ChannelInput, NotUsed> Input { get; }

    /// <summary>
    /// Output stream backed by a pre-materialized subscriber actor.
    /// Channel connects its own Sink:
    /// <code>
    /// session.Output
    ///     .To(Sink.ForEach&lt;SessionOutput&gt;(Render))
    ///     .Run(system);
    /// </code>
    /// </summary>
    public Source<SessionOutput, NotUsed> Output { get; }

    /// <summary>
    /// Gracefully shuts down both inbound and outbound streams
    /// via the shared kill switch.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _killSwitch.Shutdown();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Factory for creating per-session Akka.Streams pipelines. Injected via DI.
/// Channels call <see cref="CreateAsync"/> to get a <see cref="MaterializedSession"/>
/// without touching actor system internals.
///
/// <para>
/// Internally wires a subscriber actor (via <c>Source.PreMaterialize</c>)
/// and a command sink (via <c>Sink.ActorRef</c>) to the session manager,
/// with a shared <see cref="SharedKillSwitch"/> for coordinated teardown.
/// </para>
/// </summary>
public sealed class SessionPipeline
{
    private readonly ActorSystem _system;
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;

    public SessionPipeline(
        ActorSystem system,
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider)
    {
        _system = system;
        _sessionManagerProvider = sessionManagerProvider;
    }

    /// <summary>
    /// Creates a materialized session with typed input/output streams.
    /// </summary>
    /// <param name="sessionId">Session identity (channel owns the naming scheme).</param>
    /// <param name="options">Pipeline configuration (channel type, output filter).</param>
    /// <param name="cancellationToken">Cancellation token for session manager resolution.</param>
    /// <returns>A session handle with <see cref="MaterializedSession.Input"/> and
    /// <see cref="MaterializedSession.Output"/> streams.</returns>
    public async Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        CancellationToken cancellationToken = default)
    {
        var sessionManager = await _sessionManagerProvider.GetAsync(cancellationToken);
        var killSwitch = KillSwitches.Shared($"session-{sessionId.Value}");

        // Pre-materialize subscriber to capture IActorRef before building streams
        var (subscriber, responseSource) = Source.ActorRef<SessionOutput>(256, OverflowStrategy.DropHead)
            .PreMaterialize(_system);

        // Inbound: ChannelInput → SendUserMessage → session manager
        var inputSink = Flow.Create<ChannelInput>()
            .Select(input => MapToCommand(input, sessionId, options))
            .Via(killSwitch.Flow<SendUserMessage>())
            .To(Sink.ActorRef<SendUserMessage>(sessionManager, Done.Instance,
                ex => new Status.Failure(ex)));

        // Outbound: pre-materialized subscriber → kill switch → exposed Source
        var outputSource = responseSource
            .Via(killSwitch.Flow<SessionOutput>());

        // Join the session — subscriber starts receiving output
        sessionManager.Tell(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = options.Filter
        });

        return new MaterializedSession(inputSink, outputSource, killSwitch);
    }

    private SendUserMessage MapToCommand(
        ChannelInput input, SessionId sessionId, SessionPipelineOptions options)
    {
        // Extract text content from AIContent list (multi-modal future enhancement)
        var textParts = input.Contents.OfType<TextContent>().Select(t => t.Text);
        var content = string.Join("\n", textParts);

        return new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = new MessageSource
            {
                ChannelType = options.ChannelType,
                SenderId = input.SenderId,
                ReceivedAt = input.ReceivedAt
            }
        };
    }
}
