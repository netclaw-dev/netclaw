// -----------------------------------------------------------------------
// <copyright file="ChannelPipeline.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Options for creating a session pipeline.
/// </summary>
public sealed record SessionPipelineOptions
{
    /// <summary>
    /// Channel type identifier.
    /// Used to populate <see cref="MessageSource.ChannelType"/> on inbound messages.
    /// </summary>
    public required ChannelType ChannelType { get; init; }

    /// <summary>
    /// Which output categories the channel wants to receive.
    /// </summary>
    public OutputFilter Filter { get; init; } = OutputFilter.Full;

    /// <summary>
    /// Optional additive prompt overlay installed on the session before channel
    /// input is dispatched.
    /// </summary>
    public string? PromptOverlay { get; init; }
}

public static class MessageSourceFactory
{
    public static MessageSource Create(ChannelInput input, SessionPipelineOptions options, string turnId)
    {
        var textContent = string.Join("\n", input.Contents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        return new MessageSource
        {
            ChannelType = options.ChannelType,
            SenderId = input.SenderId,
            ChannelId = input.ChannelId,
            MessageId = input.MessageId,
            TurnId = turnId,
            Audience = input.Audience,
            Boundary = input.Boundary,
            Principal = input.Principal,
            Provenance = input.Provenance,
            ReceivedAt = input.ReceivedAt,
            ExecutableText = input.ExecutableText ?? textContent,
            HasThirdPartyAdoptedContext = input.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = input.AdoptedSpeakerIds,
            AdoptedContextProjection = input.AdoptedContextProjection,
            AdoptedContextLowerBound = input.AdoptedContextLowerBound,
            AdoptedContextUpperBound = input.AdoptedContextUpperBound,
            AdoptedContextEntries = input.AdoptedContextEntries
                .Select(entry => new MessageSource.AdoptedContextEntry(
                    entry.MessageId,
                    entry.SenderId,
                    entry.Timestamp,
                    entry.AuthorityAtInclusion))
                .ToArray(),
            ReminderId = input.ReminderId,
            AckTarget = input.AckTarget
        };
    }
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
/// Abstraction over session pipeline creation.
/// Allows test fakes to supply controlled <see cref="MaterializedSession"/> instances
/// without needing a real actor system or session manager.
/// </summary>
public interface ISessionPipeline
{
    /// <summary>Creates a materialized session for the given ID and options.</summary>
    Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends delivery feedback back to the owning session actor.
    /// </summary>
    Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default);
}

/// <summary>
/// Factory for creating per-session Akka.Streams pipelines. Injected via DI.
/// Channels call <see cref="CreateAsync"/> to get a <see cref="MaterializedSession"/>
/// without touching actor system internals.
///
/// <para>
/// Internally wires a subscriber actor (via <c>Source.PreMaterialize</c>)
/// and a <c>Sink.ForEach</c> command forwarder to the session manager,
/// with a shared <see cref="SharedKillSwitch"/> for coordinated teardown.
/// </para>
/// </summary>
public sealed class SessionPipeline : ISessionPipeline
{
    private const int SessionOutputBufferSize = 2048;

    private readonly ActorSystem _system;
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly ISessionLifecycleObserver? _lifecycleObserver;
    private readonly NetclawPaths _paths;
    private readonly SessionIngressGate? _ingressGate;

    public SessionPipeline(
        ActorSystem system,
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        NetclawPaths paths,
        ISessionLifecycleObserver? lifecycleObserver = null,
        SessionIngressGate? ingressGate = null)
    {
        _system = system;
        _sessionManagerProvider = sessionManagerProvider;
        _lifecycleObserver = lifecycleObserver;
        _paths = paths;
        _ingressGate = ingressGate;
    }

    /// <summary>
    /// Creates a materialized session with typed input/output streams.
    /// </summary>
    /// <param name="sessionId">Session identity (channel owns the naming scheme).</param>
    /// <param name="options">Pipeline configuration (channel type, output filter).</param>
    /// <param name="materializer">Optional materializer for stream actors. When provided
    /// (e.g. from an actor context), stream actors become children of the owning actor
    /// and are stopped automatically on passivation. When null, uses the system-level
    /// materializer.</param>
    /// <param name="cancellationToken">Cancellation token for session manager resolution.</param>
    /// <returns>A session handle with <see cref="MaterializedSession.Input"/> and
    /// <see cref="MaterializedSession.Output"/> streams.</returns>
    public async Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default)
    {
        _ingressGate?.ThrowIfClosed();

        var sessionManager = await _sessionManagerProvider.GetAsync(cancellationToken);
        var killSwitch = KillSwitches.Shared($"session-{sessionId.Value}");

        // Pre-materialize subscriber to capture IActorRef before building streams.
        // When a materializer is provided, the subscriber actor is a child of the
        // owning actor — stopped automatically on passivation.
        var (subscriber, responseSource) = materializer is not null
            ? Source.ActorRef<SessionOutput>(SessionOutputBufferSize, OverflowStrategy.DropHead)
                .PreMaterialize(materializer)
            : Source.ActorRef<SessionOutput>(SessionOutputBufferSize, OverflowStrategy.DropHead)
                .PreMaterialize(_system);

        // Inbound: ChannelInput → SendUserMessage → session manager (direct Tell)
        // Re-assert subscription before each message to recover from session actor
        // passivation/re-creation (JoinSession is idempotent).
        var joinMsg = new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = options.Filter
        };

        SetSessionPromptOverlay? promptOverlayMsg = null;
        if (!string.IsNullOrWhiteSpace(options.PromptOverlay))
        {
            promptOverlayMsg = new SetSessionPromptOverlay
            {
                SessionId = sessionId,
                PromptOverlay = options.PromptOverlay!.Trim()
            };
        }

        // Ordering safety: both Tell calls target the same SessionId, so
        // GenericChildPerEntityParent routes them to the same child actor.
        // Akka guarantees FIFO delivery from a single sender (this stream
        // stage thread), so JoinSession is always processed before the
        // SendUserMessage that follows it.
        var inputSink = Flow.Create<ChannelInput>()
            .Select(input => MapToCommand(input, sessionId, options, _paths))
            .Via(killSwitch.Flow<SendUserMessage>())
            .To(Sink.ForEach<SendUserMessage>(cmd =>
            {
                sessionManager.Tell(joinMsg, ActorRefs.NoSender);
                if (promptOverlayMsg is not null)
                    sessionManager.Tell(promptOverlayMsg, ActorRefs.NoSender);

                // Trusted deliveries (e.g. Mode B reminders) carry an ephemeral
                // AckTarget on their MessageSource so that the session's
                // TryReplyAck routes CommandAck/CommandNack back to the
                // dispatcher's Ask temp actor. Regular inbound ingress
                // leaves AckTarget null → existing NoSender fire-and-forget.
                var ackTarget = cmd.Source?.AckTarget ?? ActorRefs.NoSender;
                sessionManager.Tell(cmd, ackTarget);
            }).ObservingFault());

        // Outbound: pre-materialized subscriber → kill switch → exposed Source.
        // When a lifecycle observer is registered, tap the stream so every output
        // is reported regardless of which channel materializes the session.
        var outputSource = responseSource
            .Via(killSwitch.Flow<SessionOutput>());

        if (_lifecycleObserver is not null)
        {
            var observer = _lifecycleObserver;
            outputSource = outputSource.Select(output =>
            {
                observer.OnOutput(output);
                return output;
            });
        }

        // Notify observer that the session is active again (or newly created).
        _lifecycleObserver?.OnSessionActivated(sessionId, options.ChannelType);

        // Join the session — subscriber starts receiving output
        sessionManager.Tell(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = options.Filter
        }, ActorRefs.NoSender);

        return new MaterializedSession(
            inputSink,
            outputSource,
            killSwitch);
    }

    public async Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
    {
        var sessionManager = await _sessionManagerProvider.GetAsync(ct);
        sessionManager.Tell(feedback, ActorRefs.NoSender);
    }

    private static SendUserMessage MapToCommand(
        ChannelInput input,
        SessionId sessionId,
        SessionPipelineOptions options,
        NetclawPaths paths)
    {
        var turnId = string.IsNullOrWhiteSpace(input.MessageId)
            ? IdGen.ShortId()
            : input.MessageId!;

        var textParts = input.Contents.OfType<TextContent>().Select(t => t.Text);
        var content = string.Join("\n", textParts);

        // Convert DataContent items to SerializableMediaReferences, writing bytes to session media dir
        var mediaRefs = new List<SerializableMediaReference>();
        var dataContents = input.Contents.OfType<DataContent>().ToList();
        if (dataContents.Count > 0)
        {
            var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory);
            var mediaDir = Path.Combine(sessionDir, SessionDirectoryHelper.MediaSubdirectory);
            Directory.CreateDirectory(mediaDir);

            foreach (var data in dataContents)
            {
                var bytes = data.Data.ToArray();
                if (bytes.Length == 0)
                    continue;

                var mimeType = data.MediaType ?? "application/octet-stream";
                var ext = ChatMessageConverter.MimeToExtension(mimeType);
                var fileName = $"{Guid.NewGuid():N}{ext}";
                var fullPath = Path.Combine(mediaDir, fileName);

                File.WriteAllBytes(fullPath, bytes);

                mediaRefs.Add(new SerializableMediaReference
                {
                    RelativePath = fileName,
                    MimeType = mimeType,
                    Modality = (int)ChatMessageConverter.MimeToModality(mimeType),
                    FileSizeBytes = bytes.Length
                });
            }
        }

        return new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            MediaReferences = mediaRefs,
            Source = MessageSourceFactory.Create(input, options, turnId)
        };
    }
}
