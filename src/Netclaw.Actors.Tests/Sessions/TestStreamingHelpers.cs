// -----------------------------------------------------------------------
// <copyright file="TestStreamingHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Tests.Sessions;

internal static class TestStreamingHelpers
{
    public static async IAsyncEnumerable<ChatResponseUpdate> NeverCompletesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.Task;
        yield break;
    }

    /// <summary>
    /// Completes (as cancelled) only when <paramref name="ct"/> is cancelled.
    /// Lets a test fake park a stream so the consumer's watchdog or cancellation
    /// is the only thing that can end it.
    /// </summary>
    public static async Task ParkUntilCancelledAsync(CancellationToken ct)
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), parked))
            await parked.Task;
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> ReturnTextAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Compiler needs at least one await for async IAsyncEnumerable. Using
        // Task.CompletedTask here rather than Task.Yield() between updates —
        // Task.Yield() bounces every iteration through ThreadPool.QueueUserWorkItem,
        // which under Windows CI thread-pool contention can take >1s per round-trip
        // and starve callers with tight FirstTokenTimeout watchdogs.
        await Task.CompletedTask;

        var response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new TextContent(text)]));

        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }
}
