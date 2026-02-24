using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Termina.Components.Streaming;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.App.Tui;

/// <summary>
/// Animated text segment that displays elapsed time since creation.
/// Updates every second: "0s", "1s", ..., "1m 5s", etc.
/// Used alongside <see cref="SpinnerSegment"/> in tool call progress display.
/// </summary>
public sealed class ElapsedTimeSegment : IAnimatedTextSegment
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly System.Timers.Timer _timer;
    private readonly Subject<Unit> _invalidated = new();
    private readonly TextStyle _style;
    private bool _disposed;

    public IObservable<Unit> Invalidated => _invalidated.AsObservable();

    public bool IsAnimating => _timer.Enabled;

    /// <summary>
    /// The current elapsed time. Read this before disposing to capture the final value.
    /// </summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public ElapsedTimeSegment(Color? color = null, int intervalMs = 1000)
    {
        _style = new TextStyle(color ?? Color.BrightBlack);
        _timer = new System.Timers.Timer(intervalMs);
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
        Start();
    }

    public StyledSegment GetCurrentSegment()
    {
        var elapsed = _stopwatch.Elapsed;
        var text = elapsed.TotalSeconds < 60
            ? $" {elapsed.TotalSeconds:F0}s"
            : $" {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        return new StyledSegment(text, _style);
    }

    public void Start()
    {
        if (!_disposed)
            _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _timer.Dispose();
        _invalidated.OnCompleted();
        _invalidated.Dispose();
    }

    private void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_disposed)
            _invalidated.OnNext(Unit.Default);
    }
}
