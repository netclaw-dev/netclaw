// -----------------------------------------------------------------------
// <copyright file="SessionLlmInvoker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Akka.Actor;
using Microsoft.Extensions.AI;
using SessionId = Netclaw.Actors.Protocol.SessionId;
using Netclaw.Configuration;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Async pipeline for invoking the LLM. Runs on the thread pool and sends
/// results back to the session actor via <c>self.Tell()</c>.
/// </summary>
internal static class SessionLlmInvoker
{
    private static readonly TextContent EmptyTextContent = new(string.Empty);
    public static async Task InvokeAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        long callId,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        // Set session affinity so the HTTP-layer DelegatingHandler adds an
        // X-Session-Id header, keeping self-hosted LLM requests pinned to
        // the same backend for KV cache reuse. Set here (not in the actor)
        // so sidecar calls (compaction, title gen) that bypass this invoker
        // naturally omit the header and round-robin across backends.
        SessionAffinityContext.SessionId = sessionId.Value;
        using var diagnosticsScope = SessionDiagnosticsContext.Push(sessionId.Value);
        try
        {
            var response = await StreamAsync(client, messages, options, self, callId, cancellationToken);
            self.Tell(response);
        }
        catch (OperationCanceledException ex)
        {
            // The actor's ProcessingWatchdog cancelled the CTS — let the watchdog
            // handler produce the user-facing error. Send a typed failure so the
            // actor can clean up, but the watchdog message is the authoritative timeout.
            self.Tell(new LlmCallFailed(ex) { CallId = callId });
        }
        catch (Exception ex)
        {
            self.Tell(new LlmCallFailed(ex) { CallId = callId });
        }
        finally
        {
            SessionAffinityContext.SessionId = null;
        }
    }

    public static async Task<LlmResponseReceived> StreamAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        long callId,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();
        var updates = new List<ChatResponseUpdate>();
        var textBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        string? pendingTextDelta = null;
        string? pendingThinkingDelta = null;
        var textDeltaCount = 0;
        var thinkingDeltaCount = 0;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
            var dispatched = false;

            if (update.Contents is not null)
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            textBuilder.Append(text.Text);
                            textDeltaCount++;
                            if (textDeltaCount == 1)
                            {
                                pendingTextDelta = text.Text;
                                self.Tell(new LlmResponseDeltaReceived(EmptyTextContent)
                                {
                                    CallId = callId
                                });
                            }
                            else
                            {
                                if (textDeltaCount == 2 && !string.IsNullOrEmpty(pendingTextDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived(new TextContent(pendingTextDelta))
                                    {
                                        CallId = callId
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived(content) { CallId = callId });
                                dispatched = true;
                            }
                            break;

                        case TextReasoningContent thinking when !string.IsNullOrEmpty(thinking.Text):
                            thinkingBuilder.Append(thinking.Text);
                            thinkingDeltaCount++;
                            if (thinkingDeltaCount == 1)
                            {
                                pendingThinkingDelta = thinking.Text;
                                self.Tell(new LlmResponseDeltaReceived(EmptyTextContent)
                                {
                                    CallId = callId
                                });
                            }
                            else
                            {
                                if (thinkingDeltaCount == 2 && !string.IsNullOrEmpty(pendingThinkingDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived(new TextReasoningContent(pendingThinkingDelta))
                                    {
                                        CallId = callId
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived(content) { CallId = callId });
                                dispatched = true;
                            }
                            break;

                        case FunctionCallContent:
                            contents.Add(content);
                            break;
                    }
                }
            }

            // No content dispatched — send keepalive to refresh the idle timeout watchdog.
            if (!dispatched)
            {
                self.Tell(new LlmResponseDeltaReceived(EmptyTextContent)
                {
                    CallId = callId
                });
            }
        }

        if (thinkingBuilder.Length > 0)
            contents.Add(new TextReasoningContent(thinkingBuilder.ToString()));

        if (textBuilder.Length > 0)
            contents.Add(new TextContent(textBuilder.ToString()));

        var response = updates.Count > 0
            ? updates.ToChatResponse()
            : new ChatResponse(new AiChatMessage(ChatRole.Assistant, contents));

        if (response.Messages.Count == 0)
            response.Messages.Add(new AiChatMessage(ChatRole.Assistant, contents));

        return new LlmResponseReceived
        {
            Response = response,
            StreamedText = textDeltaCount > 1,
            StreamedThinking = thinkingDeltaCount > 1,
            RecallResult = null,
            CallId = callId
        };
    }
}
