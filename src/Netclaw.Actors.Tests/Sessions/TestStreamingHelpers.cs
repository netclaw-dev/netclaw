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

    public static async IAsyncEnumerable<ChatResponseUpdate> ReturnTextAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new TextContent(text)]));

        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }
}
