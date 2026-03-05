using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Short-lived child actor handling a single reminder execution.
/// Creates a session pipeline, sends the prompt, collects output,
/// and reports success/failure to parent <see cref="ReminderManagerActor"/>.
/// </summary>
internal sealed class ReminderExecutionActor : ReceiveActor
{
    private readonly ReminderPayload _payload;
    private readonly SessionPipeline _pipeline;
    private readonly ILoggingAdapter _log;

    private readonly StringBuilder _buffer = new();
    private bool _sawTextDelta;
    private bool _completed;

    private ActorMaterializer? _materializer;
    private MaterializedSession? _session;

    public static Props CreateProps(
        ReminderPayload payload,
        SessionPipeline pipeline,
        ReminderConfig config,
        TimeProvider timeProvider) =>
        Props.Create(() => new ReminderExecutionActor(payload, pipeline, config));

    private ReminderExecutionActor(
        ReminderPayload payload,
        SessionPipeline pipeline,
        ReminderConfig config)
    {
        _payload = payload;
        _pipeline = pipeline;
        _log = Context.GetLogger();

        // Set execution timeout
        Context.SetReceiveTimeout(TimeSpan.FromSeconds(config.ExecutionTimeoutSeconds));

        Receive<ExecutionOutput>(HandleOutput);
        Receive<ExecutionStarted>(_ => { }); // ack
        Receive<ReceiveTimeout>(_ =>
        {
            _log.Warning("Reminder execution timed out: '{0}'", _payload.Name);
            ReportAndStop(false, "Execution timed out");
        });
    }

    protected override void PreStart()
    {
        Self.Tell(new ExecutionStarted());
        RunTask(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Determine session ID
            var sessionId = _payload.OriginatingSessionId
                ?? new SessionId($"reminder/{_payload.Id.Value}/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

            _log.Info("Starting reminder execution for '{0}' with session '{1}'",
                _payload.Name, sessionId.Value);

            _materializer = Context.Materializer(namePrefix: "reminder-exec");

            var materialized = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
            {
                ChannelType = "reminder",
                Filter = OutputFilter.Text
            }, materializer: _materializer);

            var self = Self;

            // Wire input queue
            var inputQueue = Source.Queue<ChannelInput>(8, OverflowStrategy.Backpressure)
                .ToMaterialized(materialized.Input, Keep.Left)
                .Run(_materializer);

            // Wire output sink
            materialized.Output
                .To(Sink.ForEach<SessionOutput>(output => self.Tell(new ExecutionOutput(output))))
                .Run(_materializer);

            _session = materialized;

            // Send the prompt
            await inputQueue.OfferAsync(new ChannelInput
            {
                SenderId = "reminder-system",
                ChannelId = _payload.ReportToChannel,
                Contents = [new TextContent(_payload.Prompt)],
                ReceivedAt = DateTimeOffset.UtcNow
            });

            // Complete the input — single prompt, one turn
            inputQueue.Complete();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to initialize reminder execution for '{0}'", _payload.Name);
            ReportAndStop(false, ex.Message);
        }
    }

    private void HandleOutput(ExecutionOutput wrapper)
    {
        switch (wrapper.Output)
        {
            case TextDeltaOutput delta:
                _buffer.Append(delta.Delta);
                _sawTextDelta = true;
                break;

            case TextOutput text:
                if (!_sawTextDelta)
                    _buffer.Append(text.Text);
                break;

            case TurnCompleted:
                var result = _buffer.ToString().Trim();
                _log.Info("Reminder '{0}' execution completed, output length: {1}",
                    _payload.Name, result.Length);

                if (string.IsNullOrWhiteSpace(result))
                    result = $"(Reminder '{_payload.Name}' executed but produced no output)";

                // Log the result; Slack posting would be handled by the session
                // if the session targets a Slack channel
                _log.Info("Reminder output for '{0}': {1}",
                    _payload.Name, result.Length > 200 ? result[..200] + "..." : result);

                ReportAndStop(true);
                break;

            case ErrorOutput err:
                _log.Warning("Reminder '{0}' error output: {1}", _payload.Name, err.Message);
                ReportAndStop(false, err.Message);
                break;
        }
    }

    private void ReportAndStop(bool success, string? errorMessage = null)
    {
        if (_completed)
            return;

        _completed = true;
        Context.Parent.Tell(new ReminderExecutionCompleted(_payload.Id, success, errorMessage));
        Context.Stop(Self);
    }

    protected override void PostStop()
    {
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _materializer?.Dispose();
        base.PostStop();
    }

    private sealed record ExecutionStarted;
    private sealed record ExecutionOutput(SessionOutput Output);
}
