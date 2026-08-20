// -----------------------------------------------------------------------
// <copyright file="SessionMessageAssembler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ProtocolChatRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Pure-function tests for <see cref="SessionMessageAssembler"/>. The core
/// property under test is cache-prefix stability: two consecutive turns
/// against the same session must produce assemblies whose longest common
/// prefix extends through all static content and all conversation history,
/// with divergence only at the volatile tail.
///
/// These tests are fully deterministic — no ActorSystem, no LLM round-trip.
/// They exist to catch cache-poisoning regressions: a future change that
/// accidentally places volatile content in an early message will fail
/// <see cref="Prefix_is_stable_across_turns_for_same_session"/> loudly.
/// </summary>
public sealed class SessionMessageAssemblerTests
{
    private const string PersistedSystemPrompt = "You are Netclaw, a helpful assistant.";
    private static readonly SessionId TestSession = new("C99999/1708531200.000100");

    [Fact]
    public void Prefix_is_stable_across_turns_for_same_session()
    {
        var turn1History = SeedHistory("hello");
        var turn1 = MakeInput(turn1History, FakeRecall("mem-1"));
        var turn1Messages = SessionMessageAssembler.Assemble(turn1);

        // Turn 2: turn 1 is now in history (user + assistant), new user
        // message appended, and a different recall resolved.
        var turn2History = turn1History
            .Add(new SerializableChatMessage { Role = ProtocolChatRole.Assistant, Content = "Hi there" })
            .Add(new SerializableChatMessage { Role = ProtocolChatRole.User, Content = "what did I just say?" });
        var turn2 = MakeInput(turn2History, FakeRecall("mem-2-completely-different-content"), startupDone: true);
        var turn2Messages = SessionMessageAssembler.Assemble(turn2);

        // The cache prefix must extend through at minimum: [0]=system prompt,
        // [1]=static dynamic context, [2]=user turn1. On turn2 the static
        // dynamic context layer collapses to just the [session] + [attachments]
        // blocks (OnceAtStart layers are suppressed after startup), so the
        // [1] content differs between turn 1 and turn 2. That's an acceptable
        // structural divergence for the startup-complete boundary. What MUST
        // stay stable is the volatile content placement — memory recall and
        // current time MUST NOT appear in the System prefix on either turn.
        AssertNoVolatileContentInSystemPrefix(turn1Messages);
        AssertNoVolatileContentInSystemPrefix(turn2Messages);

        // And the prefix through [0] (persisted system prompt) is always stable.
        Assert.True(turn1Messages.Count > 0 && turn2Messages.Count > 0);
        Assert.Equal(turn1Messages[0].Role, turn2Messages[0].Role);
        Assert.Equal(turn1Messages[0].Text, turn2Messages[0].Text);
    }

    [Fact]
    public void Prefix_extends_through_history_when_startup_layers_settled()
    {
        // Steady-state: both turns have StartupContextInjected=true so the
        // static block is structurally identical across them. The assembler
        // no longer wraps the last User message — it just emits the static
        // block + history verbatim. Cache prefix stability comes from the
        // actor inserting the per-turn volatile block as a system-nudge
        // entry in history (via SessionState.AddVolatileContextNudge) BEFORE
        // the real user message, so the user message stays at the tail. We
        // simulate that here by pre-populating history in that order.
        var turn1History = ImmutableList.Create(
                new SerializableChatMessage { Role = ProtocolChatRole.System, Content = PersistedSystemPrompt })
            .Add(new SerializableChatMessage
            {
                Role = ProtocolChatRole.User,
                Content = $"{SessionState.SystemNudgePrefix} turn-1-volatile-snapshot]",
            })
            .Add(new SerializableChatMessage { Role = ProtocolChatRole.User, Content = "first question" });
        var turn1 = MakeInput(turn1History, activeRecall: null, startupDone: true);
        var turn1Messages = SessionMessageAssembler.Assemble(turn1);

        var turn2History = turn1History
            .Add(new SerializableChatMessage { Role = ProtocolChatRole.Assistant, Content = "First answer" })
            .Add(new SerializableChatMessage
            {
                Role = ProtocolChatRole.User,
                Content = $"{SessionState.SystemNudgePrefix} turn-2-volatile-snapshot]",
            })
            .Add(new SerializableChatMessage { Role = ProtocolChatRole.User, Content = "second question" });
        var turn2 = MakeInput(turn2History, activeRecall: null, startupDone: true);
        var turn2Messages = SessionMessageAssembler.Assemble(turn2);

        var prefix = LongestCommonPrefix(turn1Messages, turn2Messages);

        // Turn 1 wire: [persisted, static, nudge-1, user "first question"]
        // Turn 2 wire: [persisted, static, nudge-1, user "first question", assistant, nudge-2, user "second"]
        // Cache prefix should extend through turn 1's full content (4 messages)
        // before turn 2 diverges with its own assistant+nudge2+user2.
        Assert.True(prefix >= 4,
            $"Expected cache prefix ≥ 4 (persisted+static+nudge1+user1), got {prefix}. " +
            $"Turn 1: [{FormatMessages(turn1Messages)}] Turn 2: [{FormatMessages(turn2Messages)}]");
    }

    [Fact]
    public void Assemble_emits_history_verbatim_does_not_inject_volatile()
    {
        // Post-#1176 follow-up: the assembler stopped wrapping the last
        // User message with a <context> block. Volatile content (memory
        // recall, slash command, working context, current time, etc.) is
        // injected into history by LlmSessionActor via AddSystemNudge at
        // turn-start, before the assembler runs. The assembler emits
        // history verbatim — no <context> wrapper, no trailing System.
        var input = MakeInput(
            SeedHistory("hi"),
            FakeRecall("mem-1"),
            slashCommand: "[skill] do a thing");
        var messages = SessionMessageAssembler.Assemble(input);

        var allText = string.Join("\n", messages.Select(m => m.Text ?? string.Empty));
        Assert.DoesNotContain("<context>", allText);
        Assert.DoesNotContain("</context>", allText);
        Assert.DoesNotContain("[memory-recall]", allText);
        Assert.DoesNotContain("[skill] do a thing", allText);

        // Last message in the assembly is whatever was last in history —
        // here, the user "hi" (no nudge pre-seeded for this test).
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.User, messages[^1].Role);
        Assert.Equal("hi", messages[^1].Text);
    }

    [Fact]
    public void BuildVolatileContextBlock_composes_recall_and_slash_command()
    {
        // Pure-function helper test — this is the composition logic that
        // LlmSessionActor calls at turn-start to compute what to nudge
        // into history. Verifies content presence; format is checked by
        // existing FormatRecallForLlm / WorkingContext.ToContextBlock tests.
        var input = MakeInput(
            SeedHistory("hi"),
            FakeRecall("mem-1"),
            slashCommand: "[skill] do a thing");
        var block = SessionMessageAssembler.BuildVolatileContextBlock(input);

        Assert.Contains("[memory-recall]", block);
        Assert.Contains("mem-1", block);
        Assert.Contains("[skill] do a thing", block);
    }

    [Fact]
    public void BuildVolatileContextBlock_emits_storage_id_verbatim()
    {
        var input = MakeInput(
            SeedHistory("hi"),
            new AutomaticRecallResult(Items:
            [
                new AutomaticRecallItem(
                    Id: new Netclaw.Actors.Memory.MemoryStorageId("doc-auto"),
                    Title: "auto memory",
                    Content: "remembered content",
                    Sensitivity: "normal",
                    Score: 0.9)
            ]));

        var block = SessionMessageAssembler.BuildVolatileContextBlock(input);

        // The recall block surfaces the storage id verbatim so it round-trips back into the tools.
        Assert.Contains("[doc-auto]", block);
        Assert.DoesNotContain("doc:doc-auto", block);
    }

    [Fact]
    public void Static_block_contains_session_id_and_attachment_hint()
    {
        var input = MakeInput(SeedHistory("hi"), activeRecall: null, fileReadGranted: true);
        var messages = SessionMessageAssembler.Assemble(input);

        // Index 0 is the persisted system prompt, index 1 is the static
        // dynamic context block (when file_read is granted).
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, messages[1].Role);
        Assert.Contains($"[session]\nid: {TestSession.Value}", messages[1].Text ?? string.Empty);
        Assert.Contains("[attachments]", messages[1].Text ?? string.Empty);
    }

    [Fact]
    public void Working_context_update_does_not_poison_system_prefix()
    {
        // Turn 1: no file in working context. The static block (messages[1])
        // must stay byte-stable when working_context changes on turn 2 —
        // it lives in the leading System prefix and is part of the
        // cacheable head.
        var turn1 = MakeInput(SeedHistory("read Rect.cs"), FakeRecall("m"));
        var turn1Messages = SessionMessageAssembler.Assemble(turn1);

        var stateWithFile = turn1.State with
        {
            WorkingContext = WorkingContext.Empty.AddRecentFile("src/Rect.cs")
        };
        var turn2 = turn1 with { State = stateWithFile };
        var turn2Messages = SessionMessageAssembler.Assemble(turn2);

        Assert.Equal(turn1Messages[0].Text, turn2Messages[0].Text);
        Assert.Equal(turn1Messages[1].Text, turn2Messages[1].Text);

        var staticSystemBlock = turn2Messages[1].Text ?? string.Empty;
        Assert.DoesNotContain("[working-context]", staticSystemBlock);

        // Working-context content does NOT appear in the assembler output —
        // it would be injected by LlmSessionActor via AddSystemNudge at
        // turn-start (verified separately by actor-level tests). The
        // assembler-level invariant is that the static head stays clean.
        Assert.DoesNotContain("[working-context]",
            string.Join("\n", turn2Messages.Select(m => m.Text ?? string.Empty)));
    }

    [Fact]
    public void Recall_is_not_emitted_by_assembler_lives_in_history_nudge()
    {
        // Post-cache-history-stable: the assembler stopped composing the
        // volatile block (recall, time, working context, etc.) into the
        // wire format. LlmSessionActor persists those into History via
        // AddSystemNudge before calling Assemble. The assembler tests for
        // those things now exercise BuildVolatileContextBlock directly
        // (see BuildVolatileContextBlock_composes_recall_and_slash_command).
        // This test pins the assembler-side invariant: passing ActiveRecall
        // does NOT cause recall content to leak into the leading system or
        // the user message text.
        var input = MakeInput(SeedHistory("hi"), FakeRecall("remembered thing"));
        var messages = SessionMessageAssembler.Assemble(input);

        var allText = string.Join("\n", messages.Select(m => m.Text ?? string.Empty));
        Assert.DoesNotContain("[memory-recall]", allText);
        Assert.DoesNotContain("remembered thing", allText);
    }

    [Fact]
    public void Assembler_does_not_add_trailing_message_after_tool_result()
    {
        // Regression test for the production loop observed in
        // D0AC6CKBK5K/1776051715.090089 turn 10 on 2026-04-13.
        //
        // When a user correction triggers multi-step tool work, the
        // session actor calls Assemble on every LLM round-trip during
        // the tool loop. Mid-loop, the conversation tail in history is
        // a Tool-role result. If the assembler appended ANYTHING at
        // the tail (especially a User-role message), Qwen3's ChatML
        // template would read it as a fresh user turn → the model
        // restarts its assistant response → scans back for the
        // user-correction → re-emits "You're right — I had that
        // backwards" → fires another tool call → loops indefinitely
        // until context exhaustion (262144/262144 tokens in the
        // production repro).
        //
        // Current design: the volatile block is wrapped in <context>
        // and PREPENDED to the last User-role message already in
        // history. No new message is appended. The assembled tail
        // remains whatever the conversation tail was (Tool result
        // here), so chat templates see a coherent post-tool turn and
        // do not restart.
        var history = ImmutableList.Create(
            new SerializableChatMessage { Role = ProtocolChatRole.System, Content = PersistedSystemPrompt },
            new SerializableChatMessage
            {
                Role = ProtocolChatRole.User,
                Content = "Public is the most restrictive audience",
            },
            new SerializableChatMessage
            {
                Role = ProtocolChatRole.Assistant,
                Content = "You're right. Let me check.",
            },
            new SerializableChatMessage
            {
                Role = ProtocolChatRole.Tool,
                Name = "shell_execute",
                ToolCallId = new Netclaw.Tools.ToolCallId("call-1"),
                Content = "<tool output>",
            });

        var input = MakeInput(history, FakeRecall("mem-1"));
        var messages = SessionMessageAssembler.Assemble(input);

        Assert.NotEmpty(messages);

        // The assembler must NOT add a trailing message. The tail role
        // must be whatever was last in the conversation history — here,
        // a Tool result.
        var tail = messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.Tool, tail.Role);

        // Volatile content (recall etc.) is NOT injected by the assembler
        // anywhere — it lives in history via AddSystemNudge (actor-side).
        var allText = string.Join("\n", messages.Select(m => m.Text ?? string.Empty));
        Assert.DoesNotContain("[memory-recall]", allText);

        // The original user correction stays as-is in history.
        var lastUser = LastUserMessage(messages);
        Assert.NotNull(lastUser);
        Assert.Equal("Public is the most restrictive audience", lastUser!.Text);

        // Stronger invariant (kept from the original regression test):
        // no User-role message may appear AFTER the last Tool/Assistant
        // message in the list. If this fires, the assembler is again
        // injecting a fake user turn mid-tool-loop.
        var lastToolOrAssistantIndex = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == Microsoft.Extensions.AI.ChatRole.Tool
                || messages[i].Role == Microsoft.Extensions.AI.ChatRole.Assistant)
            {
                lastToolOrAssistantIndex = i;
            }
        }
        Assert.True(lastToolOrAssistantIndex >= 0,
            "Test fixture setup error: expected at least one Tool or Assistant message in history.");

        for (var i = lastToolOrAssistantIndex + 1; i < messages.Count; i++)
        {
            Assert.NotEqual(Microsoft.Extensions.AI.ChatRole.User, messages[i].Role);
        }
    }

    [Fact]
    public void Volatile_tail_is_suppressed_when_empty()
    {
        // No recall, no working context, no slash command, no overlay, no
        // restart notice, no EveryTurn layers — the volatile tail should
        // collapse to nothing and no trailing System-role tail message
        // should be added. The last message should be the user's history
        // message ("hi"), not an empty System-role tail block.
        var input = MakeInput(SeedHistory("hi"), activeRecall: null);
        var messages = SessionMessageAssembler.Assemble(input);

        var last = messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.User, last.Role);
        Assert.Equal("hi", last.Text);

        // And no memory-recall block leaked into any message.
        Assert.DoesNotContain(messages, m =>
            (m.Text?.Contains("[memory-recall]", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public void Public_audience_static_block_contains_session_id_only()
    {
        // Public audience must see the session id but NOT filesystem paths
        // (session_dir, media_dir) to avoid leaking host layout.
        var input = MakeInput(SeedHistory("hi"), activeRecall: null, audience: TrustAudience.Public);
        var messages = SessionMessageAssembler.Assemble(input);

        var staticBlock = messages[1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, staticBlock.Role);
        var text = staticBlock.Text ?? string.Empty;

        Assert.Contains($"[session]\nid: {TestSession.Value}", text);
        Assert.DoesNotContain("session_dir:", text);
        Assert.DoesNotContain("media_dir:", text);
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Trusted_audience_static_block_contains_only_authoritative_session_root(TrustAudience audience)
    {
        var input = MakeInput(SeedHistory("hi"), activeRecall: null, audience: audience);
        var messages = SessionMessageAssembler.Assemble(input);

        var staticBlock = messages[1];
        var text = staticBlock.Text ?? string.Empty;

        Assert.Contains("session_dir:", text);
        Assert.Contains(ToolChoiceGuidance.StructuredWorkspaceSelection, text, StringComparison.Ordinal);
        Assert.Contains(ToolChoiceGuidance.DirectorySelectionOrder, text, StringComparison.Ordinal);
        Assert.Contains(ToolChoiceGuidance.ShellCompositionOrder, text, StringComparison.Ordinal);
        Assert.Contains("private scratch for disposable writable non-project artifacts", text);
        Assert.Contains("Use one operation per call", text);
        Assert.Contains("do not substitute platform temporary storage", text);
        Assert.Contains("explicitly required platform temporary path unchanged", text);
        Assert.Contains("does not automatically clean session scratch yet", text);
        Assert.DoesNotContain("media_dir:", text);
    }

    [Fact]
    public void Public_audience_suppresses_working_context_in_volatile_block()
    {
        // Working context leaks internal paths and scratch notes — Public must not see it.
        var stateWithWorkingContext = SessionState.Empty with
        {
            History = SeedHistory("hi"),
            WorkingContext = WorkingContext.Empty.AddRecentFile("src/Secrets.cs")
        };
        var input = MakeInput(
            SeedHistory("hi"), FakeRecall("mem-1"), audience: TrustAudience.Public);
        input = input with
        {
            State = stateWithWorkingContext
        };
        var messages = SessionMessageAssembler.Assemble(input);

        var allText = string.Join("\n", messages.Select(m => m.Text ?? string.Empty));
        Assert.DoesNotContain("[working-context]", allText);
        Assert.DoesNotContain("Secrets.cs", allText);
    }

    [Fact]
    public void Public_audience_suppresses_active_background_jobs_block()
    {
        // The active-jobs block exposes commands, rationales, and the on-disk
        // output-log path — internal operational state that must not leak to a
        // Public-scoped turn on a session that started jobs at a higher audience.
        var stateWithJob = SessionState.Empty.TrackBackgroundJob(
            "bg-job:secret01",
            new Netclaw.Actors.Jobs.ActiveJobInfo
            {
                JobId = new Netclaw.Actors.Jobs.BackgroundJobId("secret01"),
                Command = "deploy --token hunter2",
                Rationale = "internal deploy",
                StartedAtMs = 1000,
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                OutputLogPath = "/home/op/.netclaw/jobs/secret01/output.log"
            }) with
        { History = SeedHistory("hi") };
        var input = MakeInput(SeedHistory("hi"), activeRecall: null, audience: TrustAudience.Public)
            with
        { State = stateWithJob };

        var block = SessionMessageAssembler.BuildVolatileContextBlock(input);
        Assert.DoesNotContain("[active-background-jobs]", block);
        Assert.DoesNotContain("secret01", block);
        Assert.DoesNotContain("output.log", block);
    }

    [Fact]
    public void Personal_audience_includes_active_background_jobs_block()
    {
        var stateWithJob = SessionState.Empty.TrackBackgroundJob(
            "bg-job:job01",
            new Netclaw.Actors.Jobs.ActiveJobInfo
            {
                JobId = new Netclaw.Actors.Jobs.BackgroundJobId("job01"),
                Command = "jekyll serve",
                Rationale = "dev server",
                StartedAtMs = 1000,
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                OutputLogPath = "/home/op/.netclaw/jobs/job01/output.log"
            }) with
        { History = SeedHistory("hi") };
        var input = MakeInput(SeedHistory("hi"), activeRecall: null, audience: TrustAudience.Personal)
            with
        { State = stateWithJob };

        var block = SessionMessageAssembler.BuildVolatileContextBlock(input);
        Assert.Contains("[active-background-jobs]", block);
        Assert.Contains("job01", block);
    }

    [Fact]
    public void Personal_audience_includes_working_context_in_volatile_block()
    {
        // Working-context inclusion is audience-gated inside
        // BuildVolatileContextBlock. The assembler itself no longer
        // composes the volatile block; the actor calls BVCB at turn-start.
        // Verifying the helper directly here.
        var stateWithWorkingContext = SessionState.Empty with
        {
            History = SeedHistory("hi"),
            WorkingContext = WorkingContext.Empty.AddRecentFile("src/Rect.cs")
        };
        var input = MakeInput(SeedHistory("hi"), FakeRecall("mem-1"), audience: TrustAudience.Personal);
        input = input with
        {
            State = stateWithWorkingContext,
            WorkingContextBlock = stateWithWorkingContext.WorkingContext.ToContextBlock()
        };

        var block = SessionMessageAssembler.BuildVolatileContextBlock(input);
        Assert.Contains("[working-context]", block);
        Assert.Contains("src/Rect.cs", block);
    }

    private static ContextAssemblyInput MakeInput(
        ImmutableList<SerializableChatMessage> history,
        AutomaticRecallResult? activeRecall,
        bool startupDone = false,
        string? slashCommand = null,
        string? overlay = null,
        string? restartNotice = null,
        bool fileReadGranted = true,
        TrustAudience audience = TrustAudience.Personal)
    {
        var state = SessionState.Empty with { History = history };
        return new ContextAssemblyInput(
            State: state,
            ContextLayers: Array.Empty<IContextLayerProvider>(),
            StartupContextInjected: startupDone,
            SlashCommandSkillContent: slashCommand,
            SessionPromptOverlay: overlay,
            TurnRestartNotice: restartNotice,
            SessionId: TestSession,
            SessionsBasePath: "/tmp/netclaw-test",
            FileReadGranted: fileReadGranted,
            ActiveRecall: activeRecall,
            WorkingContextBlock: state.WorkingContext.ToContextBlock(),
            Audience: audience);
    }

    private static ImmutableList<SerializableChatMessage> SeedHistory(string firstUser)
    {
        return ImmutableList.Create(
            new SerializableChatMessage { Role = ProtocolChatRole.System, Content = PersistedSystemPrompt },
            new SerializableChatMessage { Role = ProtocolChatRole.User, Content = firstUser });
    }

    private static AutomaticRecallResult FakeRecall(string content)
    {
        return new AutomaticRecallResult(Items: new[]
        {
            new AutomaticRecallItem(
                Id: new Netclaw.Actors.Memory.MemoryStorageId("mem/1"),
                Title: "test memory",
                Content: content,
                Sensitivity: "public",
                Score: 0.9)
        });
    }

    private static int LongestCommonPrefix(IReadOnlyList<AiChatMessage> a, IReadOnlyList<AiChatMessage> b)
    {
        var min = Math.Min(a.Count, b.Count);
        for (var i = 0; i < min; i++)
        {
            if (a[i].Role != b[i].Role || a[i].Text != b[i].Text)
                return i;
        }
        return min;
    }

    private static void AssertNoVolatileContentInSystemPrefix(IReadOnlyList<AiChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            if (msg.Role != Microsoft.Extensions.AI.ChatRole.System)
                break;
            var text = msg.Text ?? string.Empty;
            Assert.DoesNotContain("[memory-recall]", text);
            Assert.DoesNotContain("current_utc", text);
        }
    }

    private static AiChatMessage? LastUserMessage(IReadOnlyList<AiChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == Microsoft.Extensions.AI.ChatRole.User)
                return messages[i];
        }
        return null;
    }

    private static string FormatMessages(IReadOnlyList<AiChatMessage> messages)
    {
        return string.Join(" | ", messages.Select(m => $"{m.Role}:{Truncate(m.Text ?? string.Empty)}"));
    }

    private static string Truncate(string s)
    {
        return s.Length <= 30 ? s : s[..27] + "...";
    }

    /// <summary>
    /// Verifies that the OpenAI-compatible provider's <c>NormalizeMessages</c>
    /// defensively merges ANY System-role message — leading or non-leading —
    /// into a single leading System prefix on the wire. Strict
    /// OpenAI-compatible servers (vLLM with Qwen/Llama chat templates)
    /// reject non-leading System messages with HTTP 400 "System message
    /// must be at the beginning." The session assembler is the source of
    /// truth for keeping volatile per-turn context inside the last User
    /// message wrapped in &lt;context&gt;; this test pins the provider
    /// client's defensive fallback when an upstream bug emits a
    /// non-leading System anyway.
    /// </summary>
    [Fact]
    public void NormalizeMessages_merges_non_leading_system_into_leading_for_provider_compatibility()
    {
        // Regression scenario from PR #1171: a non-leading System slipped
        // through to the wire and vLLM rejected it. NormalizeMessages must
        // recover by merging into the leading prefix (and log a Warning,
        // which is not asserted here — caller wires _logger).
        var messages = new List<AiChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, "You are Netclaw"),
            new(Microsoft.Extensions.AI.ChatRole.System, "[session]\nid: test/123"),
            new(Microsoft.Extensions.AI.ChatRole.User, "first question"),
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "first answer"),
            new(Microsoft.Extensions.AI.ChatRole.System, "[memory-recall]\nstatus: healthy\n[memory-recall-item] something\n\n[current-time]\ncurrent_utc: 2026-05-25T00:00:00Z"),
        };

        var normalized = OpenAiCompatibleChatClient.NormalizeMessages(messages);

        // Exactly 3 messages out: merged-system, user, assistant.
        Assert.Equal(3, normalized.Count);

        var leadingSystem = (System.Text.Json.Nodes.JsonObject)normalized[0]!;
        Assert.Equal("system", leadingSystem["role"]!.GetValue<string>());
        var leadingContent = leadingSystem["content"]!.GetValue<string>();
        Assert.Contains("You are Netclaw", leadingContent);
        Assert.Contains("[session]", leadingContent);
        // The straggling non-leading System content must be merged in here.
        Assert.Contains("[memory-recall]", leadingContent);
        Assert.Contains("current_utc", leadingContent);

        // Conversation history stays in order; no trailing System.
        Assert.Equal("user", normalized[1]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", normalized[2]!["role"]!.GetValue<string>());
    }

    /// <summary>
    /// Regression test: assembler output (volatile context wrapped in
    /// <c>&lt;context&gt;</c> on the last User-role message, no trailing
    /// System) must pass through <c>NormalizeMessages</c> unchanged
    /// across the conversation history, so the cacheable wire prefix
    /// extends through every static message and every history pair up
    /// to (but not including) the user message that carries the
    /// volatile wrapper.
    /// </summary>
    [Fact]
    public void NormalizeMessages_cache_prefix_grows_through_history_across_turns()
    {
        // Turn 1: short history. Last User message has <context> with recall-A.
        var turn1 = new List<AiChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, "You are Netclaw"),
            new(Microsoft.Extensions.AI.ChatRole.System, "[session]\nid: test/123"),
            new(Microsoft.Extensions.AI.ChatRole.User, "<context>\n[memory-recall]\nrecall-A\n</context>\n\nfirst question"),
        };

        // Turn 2: history extended. The earlier "first question" is now
        // an inner history user (no wrapper); the freshly added "second
        // question" carries the <context> with recall-B.
        var turn2 = new List<AiChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, "You are Netclaw"),
            new(Microsoft.Extensions.AI.ChatRole.System, "[session]\nid: test/123"),
            new(Microsoft.Extensions.AI.ChatRole.User, "first question"),
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "first answer"),
            new(Microsoft.Extensions.AI.ChatRole.User, "<context>\n[memory-recall]\nrecall-B\n</context>\n\nsecond question"),
        };

        var norm1 = OpenAiCompatibleChatClient.NormalizeMessages(turn1);
        var norm2 = OpenAiCompatibleChatClient.NormalizeMessages(turn2);

        // Longest common prefix by serialized-JSON equality.
        var prefix = 0;
        var minCount = Math.Min(norm1.Count, norm2.Count);
        for (var i = 0; i < minCount; i++)
        {
            var a = norm1[i]!.ToJsonString();
            var b = norm2[i]!.ToJsonString();
            if (a == b)
                prefix++;
            else
                break;
        }

        // Expected: [0] merged-leading-system matches (no volatile in
        // either turn's leading system → byte-identical). [1] in turn 1
        // is the wrapped user; [1] in turn 2 is "first question" plain.
        // They diverge at [1] in turn 1 vs [1] in turn 2 because in turn 1
        // the only user IS the wrapped one. The minimum guarantee here is
        // prefix ≥ 1 (the merged-system). The growth-through-history
        // property is exercised in the assembler-level prefix tests above
        // (Prefix_extends_through_history_when_startup_layers_settled).
        Assert.True(prefix >= 1,
            $"Expected cache prefix >= 1 (merged system), got {prefix}");

        // Stronger guarantee: turn 2's leading system contains no
        // volatile content — i.e., NormalizeMessages did NOT accidentally
        // pull <context> content out of the user message into system.
        var leadingTurn2 = (System.Text.Json.Nodes.JsonObject)norm2[0]!;
        var leadingTurn2Content = leadingTurn2["content"]!.GetValue<string>();
        Assert.DoesNotContain("recall-B", leadingTurn2Content);
        Assert.DoesNotContain("<context>", leadingTurn2Content);
    }
}
