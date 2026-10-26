// -----------------------------------------------------------------------
// <copyright file="MaxEffortChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Providers;

/// <summary>
/// Sends the thinking level Max as the provider's own top effort. The Native dialect writes Max as
/// <see cref="ReasoningEffort.ExtraHigh"/>, because M.E.AI has no higher level, and the SDK adapters
/// send ExtraHigh as <c>xhigh</c>. A provider whose wire has a <c>max</c> effort (Anthropic,
/// OpenRouter, Venice) gets it through the raw request that the SDK adapter merges: the adapters keep
/// an effort that the raw request already sets.
/// </summary>
internal sealed class MaxEffortChatClient(IChatClient innerClient, Func<ChatOptions, object> createRawRequest)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, Prepare(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, Prepare(options), cancellationToken);

    private ChatOptions? Prepare(ChatOptions? options)
    {
        if (options?.Reasoning?.Effort != ReasoningEffort.ExtraHigh)
            return options;

        // A caller's raw request would hide the max effort, and the call would silently send xhigh.
        if (options.RawRepresentationFactory is not null)
            throw new NotSupportedException("Thinking level 'max' cannot be combined with a raw request from the caller.");

        var copy = options.Clone();
        var raw = createRawRequest(copy);
        copy.RawRepresentationFactory = _ => raw;
        return copy;
    }
}
