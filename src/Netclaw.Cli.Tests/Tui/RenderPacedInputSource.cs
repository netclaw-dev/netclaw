// -----------------------------------------------------------------------
// <copyright file="RenderPacedInputSource.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using R3;
using Termina;
using Termina.Input;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Feeds the events of a <see cref="VirtualInputSource"/> to the Termina loop one at a time. It waits
/// for the loop to render after each event. Termina 0.17.0 renders once for each batch of queued
/// events, so a script that queues many keys at once reaches a page before that page builds the
/// nodes that handle the next key. A person types one key at a time. This source does the same.
/// </summary>
internal sealed class RenderPacedInputSource : IInputSource
{

    private readonly VirtualInputSource _inner;
    private TerminaApplication? _app;

    public RenderPacedInputSource(VirtualInputSource inner) => _inner = inner;

    public void Attach(TerminaApplication app) => _app = app;

    public async Task RunAsync(ChannelWriter<object> writer, CancellationToken cancellationToken)
    {
        var app = _app ?? throw new InvalidOperationException("Attach the application before it runs.");
        var pipe = Channel.CreateUnbounded<object>();
        var forward = _inner.RunAsync(pipe.Writer, cancellationToken);

        try
        {
            // The first wait lets the loop apply a navigation request that a test queued before the run.
            await WaitForRenderAsync(app, writer, cancellationToken);
            await foreach (var evt in pipe.Reader.ReadAllAsync(cancellationToken))
            {
                await writer.WriteAsync(evt, cancellationToken);
                await WaitForRenderAsync(app, writer, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The loop cancels input sources when it shuts down. Stop feeding events.
            pipe.Writer.TryComplete();
        }

        // A cancel ends the inner source without a fault. A real fault must still surface.
        await Task.WhenAny(forward);
        if (forward.IsFaulted)
            await forward;
    }

    // The loop reads a fixed number of events in each batch and renders after the batch. The loop
    // handles the second marker in a later batch than the first one, so the render of the batch
    // that held the first marker is done when the second marker reaches Input subscribers.
    private static async Task WaitForRenderAsync(
        TerminaApplication app,
        ChannelWriter<object> writer,
        CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var marker = new ResizeEvent(0, 0);
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = app.Input.Subscribe(evt =>
            {
                if (ReferenceEquals(evt, marker))
                    seen.TrySetResult();
            });

            await writer.WriteAsync(marker, cancellationToken);
            await seen.Task.WaitAsync(cancellationToken);
        }
    }
}
