// -----------------------------------------------------------------------
// <copyright file="TuiNavigation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using R3;
using Termina;
using Termina.Input;

namespace Netclaw.Cli.Tui;

public sealed class TuiNavigation : IDisposable
{
    private readonly TuiLoopActionInputSource _loopActions = new();
    private TerminaApplication? _application;
    private IDisposable? _loopActionSubscription;
    private int _attached;

    public bool IsAttached => Volatile.Read(ref _attached) == 1;

    public void Attach(TerminaApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (Interlocked.Exchange(ref _attached, 1) == 1)
        {
            if (ReferenceEquals(_application, application))
                return;

            throw new InvalidOperationException("TUI navigation is already attached to a TerminaApplication.");
        }

        _application = application;
        application.AddInputSource(_loopActions);
        _loopActionSubscription = application.Input
            .OfType<IInputEvent, TuiLoopActionRequested>()
            .Subscribe(static action => action.Invoke());
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsAttached)
            throw new InvalidOperationException("TUI loop action was requested before TerminaApplication was attached.");

        _loopActions.Post(action);
    }

    public Task PostAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    public bool TryGoBack()
    {
        if (_application is null)
            throw new InvalidOperationException("TUI navigation was requested before TerminaApplication was attached.");

        if (!_application.CanGoBack)
            return false;

        _application.GoBack();
        return true;
    }

    public void Dispose()
    {
        _loopActionSubscription?.Dispose();
        _loopActions.Dispose();
    }
}

internal sealed record TuiLoopActionRequested(Action Action) : IInputEvent
{
    public void Invoke() => Action();
}

internal sealed class TuiLoopActionInputSource : IInputSource, IDisposable
{
    private readonly Channel<TuiLoopActionRequested> _actions = Channel.CreateUnbounded<TuiLoopActionRequested>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public void Post(Action action)
    {
        if (!_actions.Writer.TryWrite(new TuiLoopActionRequested(action)))
            throw new InvalidOperationException("TUI loop action queue has been closed.");
    }

    public async Task RunAsync(ChannelWriter<object> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var action in _actions.Reader.ReadAllAsync(cancellationToken))
                await writer.WriteAsync(action, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    public void Dispose()
    {
        _actions.Writer.TryComplete();
    }
}
