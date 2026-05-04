// -----------------------------------------------------------------------
// <copyright file="SessionMessageAssembler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Inputs for <see cref="SessionMessageAssembler.Assemble"/>. Captured as a
/// record so unit tests can drive the assembly function without spinning up
/// an ActorSystem.
/// </summary>
public sealed record ContextAssemblyInput(
    SessionState State,
    IReadOnlyList<IContextLayerProvider> ContextLayers,
    bool StartupContextInjected,
    string? SlashCommandSkillContent,
    string? SessionPromptOverlay,
    string? TurnRestartNotice,
    SessionId SessionId,
    string SessionsBasePath,
    bool FileReadGranted,
    AutomaticRecallResult? ActiveRecall,
    TrustAudience Audience = TrustAudience.Personal,
    string? SkillHint = null);

/// <summary>
/// Pure-function assembly of the <see cref="AiChatMessage"/> list sent to
/// the LLM provider on each turn.
///
/// The assembly is structured for prompt-cache prefix stability across
/// consecutive turns within a session:
///
/// <code>
/// [0]      System  persisted prompt (SOUL/AGENTS/TOOLING)
/// [1]      System  static dynamic context (OnceAtStart layers + [session] + [attachments])
/// [2..N]   User/Assistant  conversation history (from SessionState.History, minus the
///                          persisted prompt which was already at index 0)
/// [last]   User    turn-context tail: [memory-recall] + current time +
///                  [working-context] + slash command content + overlay +
///                  turn restart notice. Only emitted when non-empty.
/// </code>
///
/// The critical cache property: when the same session fires two turns in
/// sequence, the longest common prefix of both assemblies extends through
/// all static content and all conversation history up to (but not
/// including) the volatile turn-context tail. Adding a turn to history
/// extends the cache prefix for the next turn by exactly one more
/// user/assistant pair.
///
/// All volatile content (memory recall, current time, working context,
/// slash command, overlay, turn restart) is consolidated into a single
/// System-role message at the end of the list. Keeping this content out
/// of the leading System prefix means cache misses happen only for the
/// new user turn, not for the entire conversation history. The role is
/// intentionally System (not User): a trailing User-role message looks
/// to chat templates like a fresh user turn and causes the model to
/// restart an assistant response on every tool-loop iteration, which
/// produces repeating-acknowledgement loops (e.g. "You're absolutely
/// right — I had that backwards" restated on every tool call). A
/// trailing System-role message reads as scaffolding and the model
/// continues its tool work normally.
/// </summary>
public static class SessionMessageAssembler
{
    /// <summary>
    /// Attachment-handling hint injected into the dynamic system prompt
    /// when the resolved <c>ToolAudienceProfile</c> grants <c>file_read</c>.
    /// Source of truth for the netclaw-input-adapters contract's
    /// agent-facing guidance. Any edits to this string must stay in sync
    /// with <c>AttachmentNotes</c> and the canonical <c>[attachment]</c>
    /// line format.
    /// </summary>
    internal const string AttachmentContextHint =
        "[attachments]\n" +
        "Your session working directory contains an `inbox/` subdirectory where user-uploaded files are placed.\n" +
        "Each attachment is announced in the inbound message as a single line of the form:\n" +
        "    [attachment] name=\"...\" mime=\"...\" size=... path=\"inbox/...\" inlined=\"true|false\" [note=\"...\"]\n" +
        "When `inlined=\"true\"` you can see the file content natively in this turn.\n" +
        "When `inlined=\"false\"`:\n" +
        "  - If `note` begins with \"current model has no\": the file exists on disk but you cannot render it natively. " +
        "Acknowledge the attachment to the user by name in your reply and explain the limitation. Offer tool-based " +
        "workarounds where applicable (for example, `shell_execute pdftotext` for a PDF on a non-PDF model).\n" +
        "  - If `note` begins with \"format not inlineable\": use `file_read` or `shell_execute` to process the bytes. " +
        "This is the normal path for docx, zip, archive, and media files.\n" +
        "Never silently ignore an attachment the user sent — always acknowledge what you received by name, " +
        "even if you cannot fully process it.";

    public static List<AiChatMessage> Assemble(ContextAssemblyInput input)
    {
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(input.SessionId, input.SessionsBasePath);
        var messages = ChatMessageConverter.ToAiMessages(input.State.History, sessionDir);

        var staticBlock = BuildStaticContextBlock(input, sessionDir);
        if (!string.IsNullOrEmpty(staticBlock))
        {
            var staticMessage = new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, staticBlock);
            var insertIndex = 0;
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == Microsoft.Extensions.AI.ChatRole.System)
                    insertIndex = i + 1;
                else
                    break;
            }
            messages.Insert(insertIndex, staticMessage);
        }

        var volatileBlock = BuildVolatileContextBlock(input);
        if (!string.IsNullOrEmpty(volatileBlock))
        {
            messages.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, volatileBlock));
        }

        return messages;
    }

    private static string BuildStaticContextBlock(ContextAssemblyInput input, string sessionDir)
    {
        var parts = new List<string>();

        foreach (var layer in input.ContextLayers)
        {
            if (layer.Timing != ContextLayerTiming.OnceAtStart)
                continue;

            // OnceAtStart layers render only on startup (or right after a
            // compaction reset). The existing actor contract is that once
            // they've been injected, they stop appearing — that's what
            // keeps the static prefix byte-stable across turns.
            if (input.StartupContextInjected)
                continue;

            var content = layer.GetContextLayer(input.Audience);
            if (!string.IsNullOrWhiteSpace(content))
                parts.Add(content.Trim());
        }

        // Public audience sees only session id — no filesystem paths.
        if (input.Audience == TrustAudience.Public)
        {
            parts.Add($"[session]\nid: {input.SessionId.Value}");
        }
        else
        {
            var sessionBlock = $"[session]\nid: {input.SessionId.Value}"
                + $"\nsession_dir: {sessionDir}"
                + $"\nmedia_dir: {Path.Combine(sessionDir, SessionDirectoryHelper.MediaSubdirectory)}";
            parts.Add(sessionBlock);
        }

        if (input.FileReadGranted)
            parts.Add(AttachmentContextHint);

        return string.Join("\n\n", parts);
    }

    private static string BuildVolatileContextBlock(ContextAssemblyInput input)
    {
        var parts = new List<string>();

        if (input.ActiveRecall is { } recall && !recall.Degraded && recall.Items.Count > 0)
        {
            parts.Add(FormatRecallForLlm(recall));
        }
        else if (input.ActiveRecall is { Degraded: true })
        {
            parts.Add("[memory-recall]\nstatus: degraded\nreason: automatic recall unavailable for this turn");
        }

        if (input.SkillHint is not null)
            parts.Add(input.SkillHint);

        foreach (var layer in input.ContextLayers)
        {
            if (layer.Timing == ContextLayerTiming.OnceAtStart)
                continue;

            var content = layer.GetContextLayer(input.Audience);
            if (!string.IsNullOrWhiteSpace(content))
                parts.Add(content.Trim());
        }

        // Working context is suppressed for Public audience to avoid leaking
        // internal operational state (project paths, scratch notes, etc.).
        if (!input.State.WorkingContext.IsEmpty && input.Audience != TrustAudience.Public)
            parts.Add(input.State.WorkingContext.ToContextBlock());

        if (!input.State.ActiveBackgroundJobs.IsEmpty)
            parts.Add(FormatActiveBackgroundJobs(input.State));

        if (input.SlashCommandSkillContent is not null)
            parts.Add(input.SlashCommandSkillContent);

        if (input.SessionPromptOverlay is not null)
            parts.Add(input.SessionPromptOverlay);

        if (input.TurnRestartNotice is not null)
            parts.Add(input.TurnRestartNotice);

        return string.Join("\n\n", parts);
    }

    private static string FormatActiveBackgroundJobs(SessionState state)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[active-background-jobs]");
        foreach (var (_, job) in state.ActiveBackgroundJobs)
        {
            sb.Append("\n- job_id: ").Append(job.JobId);
            sb.Append("  command: ").Append(job.Command);
            if (!string.IsNullOrEmpty(job.Rationale))
                sb.Append("  rationale: ").Append(job.Rationale);
        }
        sb.Append("\nUse check_background_job to query status or cancel.");
        return sb.ToString();
    }

    private static string FormatRecallForLlm(AutomaticRecallResult recall)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[memory-recall]");
        sb.AppendLine("status: healthy");
        sb.AppendLine("mode: automatic");
        foreach (var item in recall.Items)
        {
            sb.AppendLine($"- {item.Title} [{item.Id}] sensitivity={item.Sensitivity} score={item.Score:F2}");
            sb.AppendLine($"  {item.Content}");
        }
        return sb.ToString().TrimEnd();
    }
}
