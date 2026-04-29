// -----------------------------------------------------------------------
// <copyright file="ObservationPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class ObservationPromptBuilderTests
{
    private static readonly SessionId TestSession = new("test-channel/test-thread");

    [Fact]
    public void System_prompt_describes_summarization_task()
    {
        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt(TestSession);

        Assert.Contains("session summarizer", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compress", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void System_prompt_embeds_self_session_id_for_disambiguation()
    {
        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt(TestSession);

        Assert.Contains(TestSession.Value, prompt);
        Assert.Contains("SELF session", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void System_prompt_lists_all_nine_structured_sections()
    {
        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt(TestSession);

        // Each of the nine section headers must appear in the prompt.
        Assert.Contains("## 1. Primary Request and Intent", prompt, StringComparison.Ordinal);
        Assert.Contains("## 2. Key Technical Concepts", prompt, StringComparison.Ordinal);
        Assert.Contains("## 3. Files and Code Sections", prompt, StringComparison.Ordinal);
        Assert.Contains("## 4. Problem Solving", prompt, StringComparison.Ordinal);
        Assert.Contains("## 5. Pending Tasks", prompt, StringComparison.Ordinal);
        Assert.Contains("## 6. Task Evolution", prompt, StringComparison.Ordinal);
        Assert.Contains("## 7. Current Work", prompt, StringComparison.Ordinal);
        Assert.Contains("## 8. Next Step", prompt, StringComparison.Ordinal);
        Assert.Contains("## 9. Required Files", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void System_prompt_requires_direct_quotes_in_task_evolution()
    {
        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt(TestSession);

        // The Task Evolution section is the anti-drift defense — it MUST
        // require direct user quotes rather than paraphrase.
        Assert.Contains("Direct quotes", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paraphrase", prompt, StringComparison.OrdinalIgnoreCase);
        // Section 6 is Task Evolution
        Assert.Contains("## 6. Task Evolution", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void System_prompt_instructs_preserve_prior_summary_verbatim()
    {
        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt(TestSession);

        // The second-compaction defense: when a prior summary exists, the
        // observer must preserve its sections verbatim.
        Assert.Contains("session-summary", prompt, StringComparison.Ordinal);
        Assert.Contains("preserve its sections verbatim", prompt, StringComparison.Ordinal);
        Assert.Contains("CRITICAL RULE", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void User_prompt_includes_message_content()
    {
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.User, Content = "Deploy the app to staging" },
            new() { Role = ChatRole.Assistant, Content = "I'll deploy it now." },
        };

        var prompt = ObservationPromptBuilder.BuildObservationUserPrompt(messages);

        Assert.Contains("Deploy the app to staging", prompt);
        Assert.Contains("I'll deploy it now.", prompt);
    }

    [Fact]
    public void User_prompt_skips_system_messages()
    {
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "You are a test assistant." },
            new() { Role = ChatRole.User, Content = "Hello" },
        };

        var prompt = ObservationPromptBuilder.BuildObservationUserPrompt(messages);

        Assert.DoesNotContain("You are a test assistant", prompt);
        Assert.Contains("Hello", prompt);
    }

    [Fact]
    public void User_prompt_truncates_long_tool_results_to_grounding_budget()
    {
        var longContent = new string('x', 4000);
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.Tool, Content = longContent, Name = "shell_execute", ToolCallId = "1" },
        };

        var prompt = ObservationPromptBuilder.BuildObservationUserPrompt(messages);

        Assert.True(prompt.Length < longContent.Length);
        Assert.Contains("...", prompt);
        // Budget is 1500 chars — previous implementation truncated at 500
        Assert.True(prompt.Length > 500, "Tool result budget should be larger than the old 500-char limit");
    }

    [Fact]
    public void User_prompt_includes_tool_call_names()
    {
        var messages = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls = [new SerializableToolCall { CallId = "1", Name = "shell_execute", ArgumentsJson = "{}" }]
            },
        };

        var prompt = ObservationPromptBuilder.BuildObservationUserPrompt(messages);

        Assert.Contains("shell_execute", prompt);
    }

    [Fact]
    public void User_prompt_preserves_tool_call_arguments_as_short_projection()
    {
        var messages = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = "1",
                        Name = "grep_files",
                        ArgumentsJson = """{"pattern":"Rect","path":"src/Termina.Layout"}"""
                    }
                ]
            },
        };

        var prompt = ObservationPromptBuilder.BuildObservationUserPrompt(messages);

        // The observer must be able to see WHAT was grepped, not just that grep_files was called
        Assert.Contains("grep_files", prompt);
        Assert.Contains("Rect", prompt);
        Assert.Contains("Termina.Layout", prompt);
    }

    [Fact]
    public void User_prompt_truncates_extremely_long_tool_call_arguments()
    {
        var bigBlob = new string('y', 500);
        var messages = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = "1",
                        Name = "huge_tool",
                        ArgumentsJson = $"{{\"blob\":\"{bigBlob}\"}}"
                    }
                ]
            },
        };

        var prompt = ObservationPromptBuilder.BuildObservationUserPrompt(messages);

        Assert.Contains("huge_tool", prompt);
        Assert.Contains("...", prompt);
        // Should be nowhere near 500 y's — projection clamps to ~120 chars
        Assert.DoesNotContain(new string('y', 200), prompt);
    }

    [Fact]
    public void WrapObservations_uses_session_summary_marker_with_session_id()
    {
        var text = "## 1. Primary Request and Intent\nDiscussed deployment";

        var wrapped = ObservationPromptBuilder.WrapObservations(text, TestSession);

        Assert.StartsWith($"[session-summary session:{TestSession.Value}]", wrapped);
        Assert.Contains("Primary Request and Intent", wrapped);
        Assert.Contains("Discussed deployment", wrapped);
    }

    [Fact]
    public void WrapObservations_rewrites_legacy_observations_header_to_session_summary_marker()
    {
        // Old format: "[observations from earlier in this session]" (pre-rework)
        // New format: "[session-summary session:{id}]" — the wrapper normalizes
        // any header-like first line to the canonical form.
        var text = "[observations from earlier in this session]\n- Already formatted";

        var wrapped = ObservationPromptBuilder.WrapObservations(text, TestSession);

        Assert.StartsWith($"[session-summary session:{TestSession.Value}]", wrapped);
        Assert.Contains("Already formatted", wrapped);
        Assert.DoesNotContain("observations from earlier", wrapped, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPriorSummary_returns_null_when_no_prior_summary_present()
    {
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.User, Content = "Regular user message" },
            new() { Role = ChatRole.Assistant, Content = "Regular assistant reply" },
        };

        var (prior, remaining) = ObservationPromptBuilder.ExtractPriorSummary(messages);

        Assert.Null(prior);
        Assert.Same(messages, remaining);
    }

    [Fact]
    public void ExtractPriorSummary_extracts_prior_summary_and_removes_it_from_remaining()
    {
        var priorSummaryContent = "[session-summary session:test-channel/test-thread]\n## 1. Primary Request and Intent\nPrior goal text";
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.User, Content = "Older user turn" },
            new() { Role = ChatRole.User, Content = priorSummaryContent },
            new() { Role = ChatRole.Assistant, Content = "Assistant reply after the summary" },
            new() { Role = ChatRole.User, Content = "Newer user turn" },
        };

        var (prior, remaining) = ObservationPromptBuilder.ExtractPriorSummary(messages);

        Assert.NotNull(prior);
        Assert.Equal(priorSummaryContent, prior);
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, m => (m.Content ?? "").StartsWith("[session-summary", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractPriorSummary_takes_the_most_recent_summary_when_multiple_are_present()
    {
        var first = "[session-summary session:test-channel/test-thread]\nFirst summary";
        var second = "[session-summary session:test-channel/test-thread]\nSecond summary (newer)";
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.User, Content = first },
            new() { Role = ChatRole.User, Content = "In between" },
            new() { Role = ChatRole.User, Content = second },
        };

        var (prior, _) = ObservationPromptBuilder.ExtractPriorSummary(messages);

        Assert.Equal(second, prior);
    }

    [Fact]
    public void System_prompt_includes_prior_summary_block_when_provided()
    {
        var prior = "[session-summary session:test-channel/test-thread]\n## 1. Primary Request and Intent\nOld goal: refactor the compactor";

        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt(TestSession, prior);

        Assert.Contains("PRIOR SUMMARY", prompt, StringComparison.Ordinal);
        Assert.Contains("preserve the bullets", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Old goal: refactor the compactor", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void System_prompt_without_prior_summary_matches_base_shape()
    {
        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt(TestSession, priorSummary: null);

        Assert.DoesNotContain("PRIOR SUMMARY", prompt, StringComparison.Ordinal);
        // Base prompt is still present — nine-section instructions are there.
        Assert.Contains("## 1. Primary Request and Intent", prompt, StringComparison.Ordinal);
        Assert.Contains("## 9. Required Files", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapObservations_preserves_existing_session_summary_marker_line()
    {
        // If the model emitted its own session-summary header (because it was
        // instructed to preserve a prior one), the wrapper replaces the header
        // line with the canonical header carrying the current session id.
        var text = "[session-summary session:some-other-session]\n- Body content";

        var wrapped = ObservationPromptBuilder.WrapObservations(text, TestSession);

        Assert.StartsWith($"[session-summary session:{TestSession.Value}]", wrapped);
        Assert.Contains("Body content", wrapped);
    }
}
