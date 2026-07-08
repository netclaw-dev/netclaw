// -----------------------------------------------------------------------
// <copyright file="FakeChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// A configurable, thread-safe fake <see cref="IChatClient"/> for tests that need to
/// capture what was sent and/or script what comes back. It covers the "capture and/or
/// return canned output" needs that were previously spread across a dozen near-identical
/// one-off fakes (option/message/image/context capturers, scripted-response fakes,
/// no-ops, and the sub-agent's <c>FakeChatClient</c> copy).
///
/// Streaming is intentionally trivial: it runs <see cref="GetResponseAsync"/> and replays
/// the result via <see cref="ChatResponseExtensions.ToChatResponseUpdates"/>, which also
/// surfaces <see cref="ChatResponse.Usage"/> as a streamed <c>UsageContent</c>. Tests that
/// assert on a *specific streaming shape* (parked/hanging/gated streams, mid-stream delta
/// timing, or a synchronous throw whose timing is load-bearing) are deliberately NOT served
/// by this type — those keep their own small bespoke fakes, because the stream shape is the
/// thing under test and a flag on a general fake would read worse, not better.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly object _gate = new();
    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessagesByCall = [];
    private int _callCount;

    /// <summary>Number of times a response has been requested (either path).</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Snapshot of the messages passed to the most recent call.</summary>
    public IReadOnlyList<ChatMessage>? LastReceivedMessages { get; private set; }

    /// <summary>The <see cref="ChatOptions"/> passed to the most recent call.</summary>
    public ChatOptions? LastReceivedOptions { get; private set; }

    /// <summary>Snapshot of the messages passed to every call, in order.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessagesByCall
    {
        get { lock (_gate) { return _receivedMessagesByCall.ToArray(); } }
    }

    /// <summary>When &gt; 0, the response is delayed by this amount (success path only).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>When set, every call throws this exception instead of returning a response.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Default response text when no per-call text applies. Defaults to a marker.</summary>
    public string? ResponseText { get; set; }

    /// <summary>Per-call response text, indexed by (1-based) call number.</summary>
    public IReadOnlyList<string>? ResponseTextsByCall { get; set; }

    /// <summary>
    /// When set, a qualifying call returns these tool calls instead of text.
    /// By default only the first call qualifies; see <see cref="AlwaysReturnToolCalls"/>.
    /// </summary>
    public List<FunctionCallContent>? ToolCallsOnFirstCall { get; set; }

    /// <summary>When true, every call with tools available returns the tool calls.</summary>
    public bool AlwaysReturnToolCalls { get; set; }

    /// <summary>When set, every returned response carries these token counts as <see cref="ChatResponse.Usage"/>.</summary>
    public UsageDetails? UsageOverride { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        var snapshot = messages.ToList();
        lock (_gate)
        {
            LastReceivedMessages = snapshot;
            LastReceivedOptions = options;
            _receivedMessagesByCall.Add(snapshot);
        }

        if (Failure is not null)
            throw Failure;

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        if (ToolCallsOnFirstCall is not null)
        {
            var returnToolCalls = AlwaysReturnToolCalls
                ? options?.Tools?.Count > 0
                : CallCount == 1;

            if (returnToolCalls)
            {
                var toolCallMessage = new ChatMessage(
                    ChatRole.Assistant, new List<AIContent>(ToolCallsOnFirstCall));
                return new ChatResponse(toolCallMessage) { Usage = UsageOverride };
            }
        }

        var responseText = ResponseTextsByCall is { Count: > 0 } responses && CallCount <= responses.Count
            ? responses[CallCount - 1]
            : ResponseText ?? $"[fake] Response #{CallCount}";

        var responseMessage = new ChatMessage(ChatRole.Assistant, [new TextContent(responseText)]);
        return new ChatResponse(responseMessage) { Usage = UsageOverride };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => CreateStreamingUpdatesAsync(messages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> CreateStreamingUpdatesAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
