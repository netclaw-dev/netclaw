// -----------------------------------------------------------------------
// <copyright file="SessionLlmInvoker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        // Per-content dispatch + the buffered-first-delta trick are session-specific
        // (they stream tokens to the UI and hold the first delta until a second
        // arrives so a tool-call stream is never partially rendered). The streaming
        // loop, counting, and response assembly live in StreamingResponseReader.
        var textDeltaCount = 0;
        var thinkingDeltaCount = 0;
        string? pendingTextDelta = null;
        string? pendingThinkingDelta = null;

        var result = await StreamingResponseReader.ReadAsync(
            client,
            messages,
            options,
            (update, cls, _) =>
            {
                // All deltas emitted for this update share its substantive-ness, so the
                // watchdog promotes off the prefill budget only on real output, not on a
                // content-free keepalive.
                var substantive = cls.HasSubstantiveContent;
                var dispatched = false;
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            textDeltaCount++;
                            if (textDeltaCount == 1)
                            {
                                pendingTextDelta = text.Text;
                                self.Tell(new LlmResponseDeltaReceived(EmptyTextContent) { CallId = callId, Substantive = substantive });
                            }
                            else
                            {
                                if (textDeltaCount == 2 && !string.IsNullOrEmpty(pendingTextDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived(new TextContent(pendingTextDelta))
                                    {
                                        CallId = callId,
                                        Substantive = substantive
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived(content) { CallId = callId, Substantive = substantive });
                                dispatched = true;
                            }
                            break;

                        case TextReasoningContent thinking when !string.IsNullOrEmpty(thinking.Text):
                            thinkingDeltaCount++;
                            if (thinkingDeltaCount == 1)
                            {
                                pendingThinkingDelta = thinking.Text;
                                self.Tell(new LlmResponseDeltaReceived(EmptyTextContent) { CallId = callId, Substantive = substantive });
                            }
                            else
                            {
                                if (thinkingDeltaCount == 2 && !string.IsNullOrEmpty(pendingThinkingDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived(new TextReasoningContent(pendingThinkingDelta))
                                    {
                                        CallId = callId,
                                        Substantive = substantive
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived(content) { CallId = callId, Substantive = substantive });
                                dispatched = true;
                            }
                            break;
                    }
                }

                // No content dispatched — send keepalive to refresh the watchdog. Carries
                // the update's substantive flag (e.g. a tool-call-only update is substantive
                // even though it streams no UI text; a prompt_progress heartbeat is not).
                if (!dispatched)
                {
                    self.Tell(new LlmResponseDeltaReceived(EmptyTextContent) { CallId = callId, Substantive = substantive });
                }
            },
            cancellationToken);

        return new LlmResponseReceived
        {
            Response = result.Response,
            StreamedText = result.Diagnostics.TextDeltaCount > 1,
            StreamedThinking = result.Diagnostics.ThinkingDeltaCount > 1,
            RecallResult = null,
            CallId = callId
        };
    }
}
