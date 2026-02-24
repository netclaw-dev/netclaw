using System.Reactive.Linq;
using System.Reactive.Subjects;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Termina.Reactive;

namespace Netclaw.App.Tui;

/// <summary>
/// Reactive ViewModel for the chat page. Uses <see cref="SessionPipeline"/>
/// directly (in-process, no SignalR indirection). Manages session lifecycle,
/// input submission, and output forwarding to the page.
/// </summary>
public partial class ChatViewModel : ReactiveViewModel
{
    private readonly SessionPipeline _pipeline;
    private readonly ActorSystem _system;
    private readonly TimeProvider _timeProvider;
    private readonly SessionConfig _sessionConfig;

    private MaterializedSession? _session;
    private ISourceQueueWithComplete<ChannelInput>? _inputQueue;
    private readonly Subject<SessionOutput> _outputSubject = new();

#pragma warning disable CS0169, CS0414 // Backing fields used by [Reactive] source generator
    [Reactive] private bool _isGenerating;
    [Reactive] private string _statusMessage = "Connecting...";
    [Reactive] private string? _sessionIdDisplay;
    [Reactive] private string? _usageDisplay;
#pragma warning restore CS0169, CS0414

    /// <summary>
    /// Observable stream of session output events. The page subscribes to this
    /// to render chat messages, tool activity, usage, etc.
    /// </summary>
    public IObservable<SessionOutput> SessionOutput => _outputSubject.AsObservable();

    /// <summary>
    /// The configured model identifier for display in the status bar.
    /// </summary>
    public string ModelId => _sessionConfig.ModelId;

    public int ContextWindowTokens => _sessionConfig.ContextWindowTokens;

    public ChatViewModel(
        SessionPipeline pipeline,
        ActorSystem system,
        TimeProvider timeProvider,
        SessionConfig sessionConfig)
    {
        _pipeline = pipeline;
        _system = system;
        _timeProvider = timeProvider;
        _sessionConfig = sessionConfig;
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _ = InitializeSessionAsync();
    }

    private async Task InitializeSessionAsync()
    {
        try
        {
            var sessionId = new SessionId($"tui/{Guid.NewGuid():N}");
            SessionIdDisplay = sessionId.Value;

            _session = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
            {
                ChannelType = "tui"
            });

            // Materialize output stream → forward to Subject for page rendering
            _session.Output
                .To(Sink.ForEach<Actors.Protocol.SessionOutput>(output =>
                {
                    _outputSubject.OnNext(output);

                    // Track turn lifecycle for generation state
                    switch (output)
                    {
                        case TurnCompleted:
                            IsGenerating = false;
                            break;
                        case ErrorOutput:
                            IsGenerating = false;
                            break;
                    }

                    RequestRedraw();
                }))
                .Run(_system);

            // Materialize input with queue for imperative push
            _inputQueue = Source.Queue<ChannelInput>(16, OverflowStrategy.Backpressure)
                .ToMaterialized(_session.Input, Keep.Left)
                .Run(_system);

            StatusMessage = "Ready";
            RequestRedraw();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
            RequestRedraw();
        }
    }

    /// <summary>
    /// Submit user text to the session pipeline.
    /// </summary>
    public async Task SubmitAsync(string text)
    {
        if (_inputQueue is null || string.IsNullOrWhiteSpace(text))
            return;

        IsGenerating = true;
        StatusMessage = "Generating...";

        await _inputQueue.OfferAsync(new ChannelInput
        {
            SenderId = "local-user",
            Contents = [new TextContent(text)],
            ReceivedAt = _timeProvider.GetUtcNow()
        });
    }

    public void RequestAppShutdown()
    {
        Shutdown();
    }

    public override void Dispose()
    {
        _outputSubject.Dispose();
        if (_session is not null)
        {
            _ = _session.DisposeAsync();
        }

        DisposeReactiveFields();
        base.Dispose();
    }
}
