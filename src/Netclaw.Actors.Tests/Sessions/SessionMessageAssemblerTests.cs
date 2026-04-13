using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
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
        // Simulate the steady-state condition: both turns have
        // StartupContextInjected=true, so the static block is structurally
        // identical across them. This is the case during normal multi-turn
        // operation (after the first turn fires).
        const string turn1RecallMarker = "remembered-turn-1-secret-payload";
        const string turn2RecallMarker = "completely-different-turn-2-content";

        var turn1History = SeedHistory("first question");
        var turn1 = MakeInput(turn1History, FakeRecall(turn1RecallMarker), startupDone: true);
        var turn1Messages = SessionMessageAssembler.Assemble(turn1);

        var turn2History = turn1History
            .Add(new SerializableChatMessage { Role = ProtocolChatRole.Assistant, Content = "First answer" })
            .Add(new SerializableChatMessage { Role = ProtocolChatRole.User, Content = "second question" });
        var turn2 = MakeInput(turn2History, FakeRecall(turn2RecallMarker), startupDone: true);
        var turn2Messages = SessionMessageAssembler.Assemble(turn2);

        var prefix = LongestCommonPrefix(turn1Messages, turn2Messages);

        // Expected stable prefix: [0] persisted prompt, [1] static dynamic
        // context, [2] user turn1 message, [3] assistant turn1 reply. The
        // turn 1 assistant reply is only in turn 2's history, but turn 1's
        // messages ended at the user turn1 + volatile tail. So the match
        // should extend at least through turn 1's user message.
        Assert.True(prefix >= 3,
            $"Expected cache prefix ≥ 3 messages (persisted prompt, static context, user turn1), got {prefix}. " +
            $"Turn 1: [{FormatMessages(turn1Messages)}] Turn 2: [{FormatMessages(turn2Messages)}]");

        // Guard against passing vacuously if recall was silently dropped:
        // each turn's marker must appear in its own tail, and must not
        // leak into the other turn's assembly.
        var turn1Tail = turn1Messages[^1];
        var turn2Tail = turn2Messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, turn1Tail.Role);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, turn2Tail.Role);
        Assert.Contains(turn1RecallMarker, turn1Tail.Text ?? string.Empty);
        Assert.Contains(turn2RecallMarker, turn2Tail.Text ?? string.Empty);
        Assert.DoesNotContain(turn2RecallMarker,
            string.Join("\n", turn1Messages.Select(m => m.Text ?? string.Empty)));
        Assert.DoesNotContain(turn1RecallMarker,
            string.Join("\n", turn2Messages.Select(m => m.Text ?? string.Empty)));
    }

    [Fact]
    public void Volatile_tail_message_is_System_role_at_end_of_list()
    {
        // See Volatile_tail_does_not_create_fake_user_turn_after_tool_result
        // for the full reasoning on why the role must be System, not User.
        var input = MakeInput(
            SeedHistory("hi"),
            FakeRecall("mem-1"),
            slashCommand: "[skill] do a thing");
        var messages = SessionMessageAssembler.Assemble(input);

        Assert.NotEmpty(messages);
        var tail = messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, tail.Role);
        Assert.Contains("[memory-recall]", tail.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[skill] do a thing", tail.Text ?? string.Empty, StringComparison.Ordinal);
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
        // Turn 1: no file in working context.
        var turn1 = MakeInput(SeedHistory("read Rect.cs"), FakeRecall("m"));
        var turn1Messages = SessionMessageAssembler.Assemble(turn1);

        // Turn 2: same history but working context now has a file. If
        // working-context content poisoned the static prefix, messages[1]
        // would differ between the turns. It must not.
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

        var tail = turn2Messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, tail.Role);
        Assert.Contains("[working-context]", tail.Text ?? string.Empty);
    }

    [Fact]
    public void Recall_is_not_in_leading_system_prefix_even_when_resolved()
    {
        // Cache stability requires that volatile content only appears in the
        // trailing System-role tail, never in the leading contiguous System
        // prefix that makes up the cacheable prompt.
        var input = MakeInput(SeedHistory("hi"), FakeRecall("remembered thing"));
        var messages = SessionMessageAssembler.Assemble(input);

        foreach (var msg in messages)
        {
            if (msg.Role != Microsoft.Extensions.AI.ChatRole.System)
                break;
            var text = msg.Text ?? string.Empty;
            Assert.DoesNotContain("[memory-recall]", text);
            Assert.DoesNotContain("remembered thing", text);
        }

        var tail = messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, tail.Role);
        Assert.Contains("[memory-recall]", tail.Text ?? string.Empty);
        Assert.Contains("remembered thing", tail.Text ?? string.Empty);
    }

    [Fact]
    public void Volatile_tail_does_not_create_fake_user_turn_after_tool_result()
    {
        // Regression test for the production loop observed in
        // D0AC6CKBK5K/1776051715.090089 turn 10 on 2026-04-13.
        //
        // When a user correction triggers multi-step tool work, the
        // session actor calls Assemble on every LLM round-trip during
        // the tool loop. The assembled list mid-loop looks like:
        //
        //   [system]    persisted prompt
        //   [system]    static dynamic context
        //   [user]      "Public is the most restrictive audience"
        //   [assistant] "You're right. Let me check." + tool_use(...)
        //   [tool]      <tool result>
        //   [???]       volatile tail (memory recall, time, ...)
        //
        // If the volatile tail is User-role, Qwen3's ChatML template
        // reads it as a fresh user turn → the model restarts its
        // assistant response → it scans back for recent user content to
        // acknowledge → finds the correction → re-emits "You're right
        // — I had that backwards" → fires another tool call → loops
        // indefinitely until context exhaustion (262144/262144 tokens
        // in the production repro).
        //
        // The fix is to emit the volatile tail as a System-role
        // message. System-role messages at the end read as scaffolding
        // and the model continues its tool work normally.
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
                ToolCallId = "call-1",
                Content = "<tool output>",
            });

        var input = MakeInput(history, FakeRecall("mem-1"));
        var messages = SessionMessageAssembler.Assemble(input);

        Assert.NotEmpty(messages);

        // The volatile tail must be the last message AND it must be
        // System-role (not User), otherwise the chat template sees it
        // as a new user turn.
        var tail = messages[^1];
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, tail.Role);
        Assert.Contains("[memory-recall]", tail.Text ?? string.Empty);

        // Stronger invariant: no User-role message may appear AFTER
        // the last Tool/Assistant message in the list. If this fires,
        // the assembler is injecting a fake user turn mid-tool-loop
        // and we are about to regress the production loop.
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

    private static ContextAssemblyInput MakeInput(
        ImmutableList<SerializableChatMessage> history,
        AutomaticRecallResult? activeRecall,
        bool startupDone = false,
        string? slashCommand = null,
        string? overlay = null,
        string? restartNotice = null,
        bool fileReadGranted = true)
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
            ActiveRecall: activeRecall);
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
                Id: "mem/1",
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

    private static string FormatMessages(IReadOnlyList<AiChatMessage> messages)
    {
        return string.Join(" | ", messages.Select(m => $"{m.Role}:{Truncate(m.Text ?? string.Empty)}"));
    }

    private static string Truncate(string s)
    {
        return s.Length <= 30 ? s : s[..27] + "...";
    }
}
