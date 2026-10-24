// -----------------------------------------------------------------------
// <copyright file="ChatClientCloseExpiryScenario.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Netclaw.Cli.Daemon;
using static Netclaw.Cli.Daemon.ChatClientProtocol;

namespace Netclaw.Cli.Tests.Cli;

// The caller supplies a system whose scheduler stays stationary throughout this scenario.
// This scenario has no xUnit or FakeTimeProvider dependency. Both test frameworks can call it.
internal static class ChatClientCloseExpiryScenario
{
    internal const string Text = "text behind held bind";
    private static readonly TimeSpan FailureCeiling = TimeSpan.FromSeconds(10);
    internal sealed record Result(ChatCloseReceipt Receipt, Exception? SendError, string[] SentText);

    internal static async Task<Result> RunAsync(ActorSystem system, TimeProvider clock,
        Action clockAdvance, CancellationToken token)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeDaemonHubTransport
        {
            EnsureSessionGate = _ => { entered.TrySetResult(); return release.Task; }
        };
        var events = Channel.CreateUnbounded<ClientEvent>();
        var owner = system.ActorOf(Props.Create(() => new ChatClientActor(transport, "http://localhost",
            new[] { TimeSpan.Zero }, TimeSpan.FromSeconds(60), clock, events.Writer, stopped)));
        Task<Exception?>? outcome = null;
        try
        {
            var open = new Open(null, null);
            owner.Tell(open, ActorRefs.NoSender);
            await open.Completion.WaitAsync(FailureCeiling, token);
            var send = new SendText(Text, token);
            outcome = ObserveAsync(send.Completion);
            owner.Tell(send, ActorRefs.NoSender);
            await entered.Task.WaitAsync(FailureCeiling, token);

            var reply = new TaskCompletionSource<ChatCloseReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Tell(new Close(reply, clock.GetTimestamp()), ActorRefs.NoSender);
            while (await events.Reader.ReadAsync(token).AsTask().WaitAsync(FailureCeiling, token)
                   is not ConnectionChanged { Value.State: DaemonConnectionState.Closing }) { }

            // Only the elapsed clock advances. CloseDeadline cannot overtake the held bind completion.
            clockAdvance();
            release.TrySetResult();
            var error = await outcome.WaitAsync(FailureCeiling, token);
            var receipt = await reply.Task.WaitAsync(FailureCeiling, token);
            system.Stop(owner);
            await stopped.Task.WaitAsync(FailureCeiling, token);
            return new Result(receipt, error, transport.Invocations.Where(call => call.Method == "SendMessage")
                .Select(call => (string)call.Args[1]!).ToArray());
        }
        finally
        {
            release.TrySetResult();
            system.Stop(owner);
            await stopped.Task.WaitAsync(FailureCeiling, token);
            if (outcome is not null) await outcome.WaitAsync(FailureCeiling, token);
            await transport.DisposeAsync();
        }
    }

    internal static async Task<Exception?> ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }
}
