// -----------------------------------------------------------------------
// <copyright file="FakeChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Minimal IChatClient test double.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? _handler;
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? _streamHandler;

    public FakeChatClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? handler = null,
        bool streaming = false,
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? streamHandler = null)
    {
        _ = streaming;
        _handler = handler;
        _streamHandler = streamHandler;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_handler is not null)
            return _handler(messages, options, cancellationToken);

        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "default")]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_streamHandler is not null)
            return _streamHandler(messages, options, cancellationToken);

        return StreamAsync();
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync()
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streamed")]
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
