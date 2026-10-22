// -----------------------------------------------------------------------
// <copyright file="HeadlessChannelStopTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// The run thread of <c>netclaw chat -p</c> owns the session log writer. When <see cref="HeadlessChannel.StopAsync"/>
/// returns, the writer must be closed. Windows cannot delete an open file, so a caller that deletes the logs folder
/// after the stop fails when the writer is still open (#2597).
/// </summary>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessChannelStopTests : IAsyncDisposable
{
    // A fault ceiling. A healthy run passes each wait within milliseconds. No step orchestrates with this wait.
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly FakeDaemonHubTransport _transport = new();
    private readonly DaemonClient _client;
    private readonly DisposableTempDir _directory = new();

    public HeadlessChannelStopTests() => _client = new("http://127.0.0.1:1", _transport, reconnectDelays: [TimeSpan.Zero]);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _directory.Dispose();
    }

    [Fact]
    public async Task StopAsync_returns_only_after_the_run_thread_closed_the_session_log()
    {
        _transport.VoidInvokeHook = (method, _, _) =>
        {
            if (method == "SendMessage")
                _transport.PushOutput(SessionOutputDtoMapper.ToDto(new TurnCompleted
                {
                    SessionId = new SessionId("fake/session"),
                    TurnNumber = new TurnNumber(1),
                    Outcome = TurnOutcome.Completed,
                    OpenChildRunIds = []
                }));
            return Task.CompletedTask;
        };
        var paths = new NetclawPaths(_directory.Path);
        var lifetime = new HoldingLifetime();
        var channel = new HeadlessChannel(_client, paths, lifetime, TimeProvider.System,
            new HeadlessOptions("Do the task."), NullLogger<HeadlessChannel>.Instance);

        var originalOut = Console.Out;
        Console.SetOut(new LockedCapture());
        try
        {
            await channel.StartAsync(TestContext.Current.CancellationToken);

            // The run ended. The lifetime holds the run thread in StopApplication, before the writer closes.
            await lifetime.Entered.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var stop = channel.StopAsync(CancellationToken.None);
            Assert.False(stop.IsCompleted, "StopAsync returned while the run thread still had the session log open.");

            lifetime.Release();
            await stop.WaitAsync(Ceiling, TestContext.Current.CancellationToken);

            // An exclusive open fails while a writer has the file open, on Windows and on Linux.
            var log = Assert.Single(Directory.GetFiles(paths.LogsDirectory, "*.log"));
            using var exclusive = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            lifetime.Release();
            Console.SetOut(originalOut);
            Environment.ExitCode = 0;
        }
    }

    private sealed class HoldingLifetime : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();

        public Task Entered => _entered.Task;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Release() => _release.Set();

        public void StopApplication()
        {
            _entered.TrySetResult();
            if (!_release.Wait(Ceiling))
                throw new TimeoutException($"The test did not release the run thread within {Ceiling}.");
        }
    }
}
