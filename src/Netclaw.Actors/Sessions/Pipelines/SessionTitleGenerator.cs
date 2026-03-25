using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Fires a best-effort sidecar LLM call to generate a short session title.
/// Failures are silently logged and ignored.
/// </summary>
internal static class SessionTitleGenerator
{
    /// <summary>
    /// Determines whether a title generation attempt should fire for the given turn count.
    /// </summary>
    public static bool ShouldGenerate(int turnCount, int interval)
    {
        if (interval <= 0) return false;
        return turnCount == 1 || turnCount % interval == 0;
    }

    /// <summary>
    /// Async pipeline: generates a session title from conversation history and tells
    /// <paramref name="self"/> the result via <see cref="TitleGenerationCompleted"/>.
    /// Runs on the thread pool -- must not touch actor state.
    /// </summary>
    public static async Task GenerateAsync(
        IChatClient client,
        IReadOnlyList<SerializableChatMessage> history,
        IActorRef self,
        ILoggingAdapter log,
        TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var messages = new List<AiChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.User,
                    CompactionPromptBuilder.BuildTitleGenerationPrompt(history))
            };
            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var title = response.Messages[^1].Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title))
            {
                log.Warning("Sidecar title generation returned null/whitespace text");
                return;
            }

            self.Tell(new TitleGenerationCompleted { Title = title });
        }
        catch (Exception ex)
        {
            // Title generation is best-effort -- log and move on
            log.Warning("Sidecar title generation failed: {0}", ex.Message);
        }
    }
}
