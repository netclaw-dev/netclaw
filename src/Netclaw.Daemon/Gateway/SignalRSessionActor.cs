using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.AspNetCore.SignalR;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Per-session binding actor for SignalR connections.
/// Owns a <see cref="ActorMaterializer"/> scoped to this actor context so that
/// all Akka.Streams stage actors become children and are automatically stopped
/// when this actor stops — eliminating the StreamSupervisor actor leak that
/// occurred when streams were materialized at the system level.
/// </summary>
/// <remarks>
/// Mirrors the pattern used by <c>SlackThreadBindingActor</c> for Slack sessions.
/// </remarks>
internal sealed class SignalRSessionActor : ReceiveActor, IWithUnboundedStash, IWithTimers
{
    private readonly SessionId _sessionId;
    private readonly ISessionPipeline _pipeline;
    private readonly IHubContext<SessionHub, ISessionHubClient> _hubContext;
    private readonly ILoggingAdapter _log;

    private ActorMaterializer? _materializer;
    private MaterializedSession? _session;
    private ChannelWriter<ChannelInput>? _inputQueue;
    private SignalRConnectionId _currentConnectionId;
    private Actors.Channels.ChannelType _channelType = Actors.Channels.ChannelType.Tui;
    private int _pipelineGeneration;
    private bool _isReinitializing;

    private static readonly TimeSpan PipelineInitTimeout = TimeSpan.FromSeconds(15);
    private static readonly object ReinitializeTimerKey = new();

    public IStash Stash { get; set; } = null!;
    public ITimerScheduler Timers { get; set; } = null!;

    public SignalRSessionActor(
        string entityId,
        ISessionPipeline pipeline,
        IHubContext<SessionHub, ISessionHubClient> hubContext)
    {
        _sessionId = new SessionId(entityId);
        _pipeline = pipeline;
        _hubContext = hubContext;
        _log = Context.GetLogger()
            .WithContext("Adapter", "signalr")
            .WithContext("SessionId", _sessionId.Value);

        Initializing();
    }

    public static Props CreateProps(string entityId, ISessionPipeline pipeline,
        IHubContext<SessionHub, ISessionHubClient> hubContext)
        => Props.Create(() => new SignalRSessionActor(entityId, pipeline, hubContext));

    private void Initializing()
    {
        ReceiveAsync<StartSignalRSession>(async msg =>
        {
            _channelType = msg.ChannelType;
            _currentConnectionId = msg.ConnectionId;

            try
            {
                await EnsureInitializedAsync();
                Become(Active);
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize SignalR session pipeline; stopping actor");
                Context.Stop(Self);
            }
        });

        // A reminder can fire against a passivated session with no
        // connected client, so we initialize the pipeline lazily without
        // a prior StartSignalRSession. Output is dropped if no client is
        // connected; the turn still persists via TurnRecorded.
        ReceiveAsync<DeliverTrustedSessionTurn>(async msg =>
        {
            try
            {
                await EnsureInitializedAsync();
                Become(Active);
                Self.Forward(msg);
                Stash.UnstashAll();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize SignalR session pipeline for Mode B reminder; stopping actor");
                Sender.Tell(CommandNack.For(_sessionId, $"SignalR pipeline init failed: {ex.Message}"));
                Context.Stop(Self);
            }
        });

        ReceiveAny(_ => Stash.Stash());
    }

    private void Active()
    {
        Receive<AttachSignalRConnection>(msg =>
        {
            _currentConnectionId = msg.ConnectionId;
            _log.Debug("Connection {ConnectionId} attached to session", msg.ConnectionId.Value);
        });

        ReceiveAsync<EnqueueSignalRInput>(async msg =>
        {
            var writer = _inputQueue;
            if (writer is null)
            {
                _log.Warning("Input queue not initialized; dropping message for session {SessionId}", _sessionId.Value);
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await writer.WriteAsync(msg.Input, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("Timed out writing to input queue for session {SessionId}", _sessionId.Value);
            }
            catch (ChannelClosedException)
            {
                _log.Warning("Input queue closed for session {SessionId}; reinitializing", _sessionId.Value);
                Self.Tell(new ReinitializePipeline("input queue closed"));
            }
        });

        Receive<OutputReceived>(msg =>
        {
            if (_currentConnectionId == default)
                return;

            var dto = SessionOutputDtoMapper.ToDto(msg.Output);
            _hubContext.Clients.Client(_currentConnectionId.Value)
                .ReceiveOutput(dto)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _log.Debug(t.Exception,
                            "Failed to deliver output to connection {ConnectionId}", _currentConnectionId.Value);
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
        });

        Receive<OutputStreamTerminated>(msg =>
        {
            if (msg.Generation != _pipelineGeneration)
                return;

            var reason = msg.Cause is null
                ? "completed"
                : $"faulted: {msg.Cause.Message}";

            _log.Warning("SignalR output stream terminated ({Reason}); reinitializing pipeline", reason);
            Self.Tell(new ReinitializePipeline(reason));
        });

        ReceiveAsync<ReinitializePipeline>(async msg => await ReinitializePipelineAsync(msg.Reason));

        Receive<ShutdownSignalRSession>(_ =>
        {
            _log.Debug("Shutdown requested for session {SessionId}", _sessionId.Value);
            Context.Stop(Self);
        });

        ReceiveAsync<DeliverTrustedSessionTurn>(async msg =>
        {
            var ackTarget = Sender;

            var writer = _inputQueue;
            if (writer is null)
            {
                _log.Warning(
                    "SignalR input queue not initialized; rejecting Mode B reminder for {SessionId}",
                    _sessionId.Value);
                ackTarget.Tell(CommandNack.For(_sessionId, "SignalR pipeline not initialized"));
                return;
            }

            var input = new ChannelInput
            {
                SenderId = msg.Source.SenderId,
                ChannelId = msg.Source.ChannelId,
                MessageId = msg.Source.MessageId,
                Audience = msg.Source.Audience,
                Boundary = msg.Source.Boundary,
                Principal = msg.Source.Principal,
                Provenance = msg.Source.Provenance,
                Contents = [new Microsoft.Extensions.AI.TextContent(msg.Content)],
                ReceivedAt = msg.Source.ReceivedAt,
                ReminderId = msg.Source.ReminderId,
                AckTarget = ackTarget
            };

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await writer.WriteAsync(input, cts.Token);
                _log.Debug(
                    "reminder_mode_b_dispatch session={Session} reminder={Reminder}",
                    _sessionId.Value, msg.Source.ReminderId);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("Timed out enqueueing Mode B reminder for session {SessionId}", _sessionId.Value);
                ackTarget.Tell(CommandNack.For(_sessionId, "SignalR pipeline enqueue timeout"));
            }
            catch (ChannelClosedException)
            {
                _log.Warning("SignalR input queue closed; rejecting Mode B reminder for {SessionId}", _sessionId.Value);
                ackTarget.Tell(CommandNack.For(_sessionId, "SignalR input queue closed"));
                Self.Tell(new ReinitializePipeline("input queue closed during Mode B delivery"));
            }
        });
    }

    protected override void PostStop()
    {
        _inputQueue?.TryComplete();
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _materializer?.Dispose();
        base.PostStop();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_session is not null)
            return;

        _log.Info("Initializing SignalR session pipeline");
        var self = Self;

        _materializer = Context.Materializer(namePrefix: "signalr");

        using var initCts = new CancellationTokenSource(PipelineInitTimeout);
        var materialized = await _pipeline.CreateAsync(
            _sessionId,
            new SessionPipelineOptions
            {
                ChannelType = _channelType,
                DefaultAudience = TrustAudience.Personal,
                DefaultBoundary = SecurityPolicyDefaults.LocalDaemonBoundary,
                DefaultPrincipal = PrincipalClassification.Operator,
                DefaultProvenance = new SourceProvenance
                {
                    TransportAuthenticity = TransportAuthenticity.LocalProcess,
                    PayloadTaint = PayloadTaint.Trusted,
                    SourceKind = "signalr"
                }
            },
            materializer: _materializer,
            cancellationToken: initCts.Token);

        var inputQueue = Source.Channel<ChannelInput>(512, true)
            .ToMaterialized(materialized.Input, Keep.Left)
            .Run(_materializer);

        var generation = ++_pipelineGeneration;
        var outputCompletion = materialized.Output
            .ToMaterialized(
                Sink.ForEach<SessionOutput>(output => self.Tell(new OutputReceived(output))),
                Keep.Right)
            .Run(_materializer);

        _ = outputCompletion.ContinueWith(t =>
            {
                var cause = t.IsFaulted ? t.Exception?.GetBaseException() : null;
                self.Tell(new OutputStreamTerminated(generation, cause));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _session = materialized;
        _inputQueue = inputQueue;

        _log.Info("SignalR session pipeline initialized");
    }

    private async Task ReinitializePipelineAsync(string reason)
    {
        if (_isReinitializing)
            return;

        _isReinitializing = true;
        try
        {
            _log.Warning("Reinitializing SignalR session pipeline: {Reason}", reason);

            _inputQueue?.TryComplete();
            _inputQueue = null;

            if (_session is not null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            _materializer?.Dispose();
            _materializer = null;

            await EnsureInitializedAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SignalR pipeline reinitialization failed; scheduling retry");
            Timers.StartSingleTimer(
                ReinitializeTimerKey,
                new ReinitializePipeline("retry after failed reinit"),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            _isReinitializing = false;
        }
    }

    // ─── Message protocol ───────────────────────────────────────────────────

    private sealed record OutputReceived(SessionOutput Output);
    private sealed record OutputStreamTerminated(int Generation, Exception? Cause);
    private sealed record ReinitializePipeline(string Reason);
}

// ─── Gateway-routable messages (implement ISignalRSessionMessage) ────────────

/// <summary>Marker for messages routable to a <see cref="SignalRSessionActor"/>.</summary>
internal interface ISignalRSessionMessage
{
    SessionId SessionId { get; }
}

/// <summary>Creates (or re-creates) a SignalR session binding actor.</summary>
internal sealed record StartSignalRSession(
    SessionId SessionId,
    Actors.Channels.ChannelType ChannelType,
    SignalRConnectionId ConnectionId) : ISignalRSessionMessage;

/// <summary>Updates the active connection ID for an existing session.</summary>
internal sealed record AttachSignalRConnection(
    SessionId SessionId,
    SignalRConnectionId ConnectionId) : ISignalRSessionMessage;

/// <summary>Delivers a user message to the session's input pipeline.</summary>
internal sealed record EnqueueSignalRInput(
    SessionId SessionId,
    ChannelInput Input) : ISignalRSessionMessage;

/// <summary>Requests graceful actor shutdown and stream cleanup.</summary>
internal sealed record ShutdownSignalRSession(SessionId SessionId) : ISignalRSessionMessage;

/// <summary>
/// Routes messages to <see cref="SignalRSessionActor"/> children by session ID.
/// Channel-internal messages implement <see cref="ISignalRSessionMessage"/>
/// and match first. Upstream protocol messages (e.g.
/// <see cref="Actors.Protocol.DeliverTrustedSessionTurn"/> for Mode B reminder
/// re-entry) implement the public <see cref="Actors.Protocol.IWithSessionId"/>
/// interface and are routed via the fallback pattern below — this keeps
/// <see cref="ISignalRSessionMessage"/> <c>internal</c> while still allowing
/// shared protocol messages to reach the correct session actor.
/// </summary>
internal sealed class SignalRMessageExtractor : Akka.Cluster.Sharding.HashCodeMessageExtractor
{
    public SignalRMessageExtractor() : base(40) { }

    public override string? EntityId(object message) => message switch
    {
        ISignalRSessionMessage msg => msg.SessionId.Value,
        Actors.Protocol.IWithSessionId wid => wid.SessionId.Value,
        _ => null
    };
}
