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
    string WorkingContextBlock,
    TrustAudience Audience = TrustAudience.Personal,
    string? SkillHint = null,
    // Maps canonical tool names (the form persisted in history) back
    // to the LLM-facing alias the model expects on the wire. Anthropic
    // rejects `/` in tool names; if this is null and history contains
    // MCP tool calls, the LLM provider will return 400. Production
    // callers should pass `toolRegistry.ToLlmFacingName`; unit tests
    // that don't exercise MCP can leave it null.
    Func<string, string>? ToolNameToLlmFacing = null,
    // When set, media references whose modality is not supported by the
    // active model are dropped from the wire message list. The assembler
    // injects a volatile system notice when any media is stripped.
    ModelModality SupportedInputModalities = ModelModality.Text | ModelModality.Image | ModelModality.Audio | ModelModality.Video);

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
/// [2..N]   User/Assistant/Tool  conversation history (from SessionState.History,
///                               minus the persisted prompt which was already at index 0).
///                               The actor injects the turn's volatile context as a
///                               User-role <c>[system: ...]</c> nudge entry in
///                               History at turn-start (see remarks).
/// </code>
///
/// The critical cache property: when the same session fires two turns in
/// sequence, the longest common prefix of both assemblies extends through
/// the leading System prefix and the ENTIRE conversation history. Adding
/// a turn extends the cache prefix for the next turn by every user, nudge,
/// assistant, and tool message of the completed turn.
///
/// All volatile content (memory recall, current time, working context,
/// skill hint, slash command, overlay, turn restart, background jobs) is
/// composed by <see cref="BuildVolatileContextBlock"/> and persisted into
/// History via <c>SessionState.AddSystemNudge</c> at turn-start by the
/// actor — NOT here. That placement satisfies the constraints that the
/// previous design discovered the hard way:
///
/// <list type="bullet">
///   <item>Volatile bytes live inside History, so on every subsequent
///   turn's assembly they are reproduced verbatim — the cache prefix
///   extends through every prior turn's volatile region as well.</item>
///   <item>No trailing System-role message is emitted, so strict
///   OpenAI-compatible servers (vLLM with Qwen/Llama chat templates)
///   don't reject the request with "System message must be at the
///   beginning." (PR #1171's regression.)</item>
///   <item>No message is appended after the last conversation message,
///   so mid-tool-loop assemblies (where the tail is a Tool result)
///   don't accidentally inject a fake User turn that would make chat
///   templates restart the assistant response and produce
///   repeating-acknowledgement loops.</item>
/// </list>
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
        "The announced `path` is authoritative, relative to `session_dir`, and already includes any collision-safe filename change. " +
        "Use `{session_dir}/{path}` when you need the absolute path on the host; do not search other session subdirectories for another copy.\n" +
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
        var messages = ChatMessageConverter.ToAiMessages(
            input.State.History,
            sessionDir,
            toolNameResolver: input.ToolNameToLlmFacing,
            supportedModalities: input.SupportedInputModalities);

        // Inject volatile notice when media was stripped from history
        var strippedCount = ChatMessageConverter.CountStrippedMedia(
            input.State.History, input.SupportedInputModalities);
        if (strippedCount > 0)
        {
            var notice = BuildMediaStrippedNotice(strippedCount, input.SupportedInputModalities);
            messages.Insert(0, new AiChatMessage(
                Microsoft.Extensions.AI.ChatRole.User,
                notice));
        }

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

        // The volatile per-turn context (memory recall, current time,
        // working context, slash command body, etc.) is NOT injected
        // here. It is composed by LlmSessionActor at turn-start and
        // persisted into History via SessionState.AddSystemNudge so the
        // wire bytes for that content are byte-stable across consecutive
        // turns (the cache prefix can extend through history naturally
        // on every byte-prefix-caching provider). The assembler now
        // just emits the static head + history verbatim.
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
            var sessionBlock = $"[session]\nid: {input.SessionId.Value}" + $"\nsession_dir: {sessionDir}";
            parts.Add(sessionBlock);
        }

        if (input.FileReadGranted)
            parts.Add(AttachmentContextHint);

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Composes the per-turn volatile context block — memory recall, current
    /// time, working context, skill hint, slash command body, session prompt
    /// overlay, turn restart notice, active background jobs. Exposed
    /// <c>internal</c> so <c>LlmSessionActor</c> can call this at turn-start
    /// and persist the result into history via <c>AddSystemNudge</c>; that
    /// makes the wire bytes for the volatile content byte-stable across turns
    /// (the cache prefix can extend through history naturally). The assembler
    /// itself no longer injects this block — it just emits the assembled
    /// history verbatim, with the nudge already in place.
    /// </summary>
    internal static string BuildVolatileContextBlock(ContextAssemblyInput input)
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
        if (!string.IsNullOrWhiteSpace(input.WorkingContextBlock) && input.Audience != TrustAudience.Public)
            parts.Add(input.WorkingContextBlock);

        // Suppressed for Public audience, same as WorkingContext: the block
        // exposes internal operational state — commands, rationales, and the
        // on-disk output-log path — that must not leak to a Public-scoped turn
        // on a session that started jobs at a higher audience.
        if (!input.State.ActiveBackgroundJobs.IsEmpty && input.Audience != TrustAudience.Public)
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
        var anyReaped = false;
        foreach (var (_, job) in state.ActiveBackgroundJobs)
        {
            sb.Append("\n- job_id: ").Append(job.JobId);
            sb.Append("  command: ").Append(job.Command);
            if (job.ReapedAtMs is not null)
            {
                anyReaped = true;
                sb.Append("  status: reaped");
            }

            if (!string.IsNullOrEmpty(job.Rationale))
                sb.Append("  rationale: ").Append(job.Rationale);
            if (!string.IsNullOrEmpty(job.OutputLogPath))
                sb.Append("\n  output log: ").Append(job.OutputLogPath);
        }

        sb.Append("\nUse check_background_job to query status or cancel.");
        if (anyReaped)
            sb.Append("\nJobs marked 'reaped' were killed when this session went idle (passivation) — "
                      + "their processes are gone; resubmit if still needed. Their output logs remain readable.");
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
            sb.AppendLine($"- {item.Title} [{item.Id.Value}] sensitivity={item.Sensitivity} score={item.Score:F2}");
            sb.AppendLine($"  {item.Content}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Build a volatile (non-persisted) system notice when media references
    /// were stripped from the wire message list because the active model does
    /// not support their modality.
    /// </summary>
    internal static string BuildMediaStrippedNotice(int strippedCount, ModelModality supported)
    {
        var required = string.Empty;
        if ((supported & ModelModality.Image) == 0)
            required = "image";
        if ((supported & ModelModality.Audio) == 0)
            required = (required.Length > 0 ? required + ", " : "") + "audio";
        if ((supported & ModelModality.Video) == 0)
            required = (required.Length > 0 ? required + ", " : "") + "video";

        return $"[system: media-filtered] {strippedCount} media reference(s) from earlier in this " +
            $"conversation were omitted because the current model does not support " +
            $"{(required.Length > 0 ? required : "this")} input. " +
            "Switch to a multimodal model in your Netclaw configuration to view them.";
    }
}
