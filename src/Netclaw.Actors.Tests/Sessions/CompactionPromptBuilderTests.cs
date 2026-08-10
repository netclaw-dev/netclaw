// -----------------------------------------------------------------------
// <copyright file="CompactionPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tests for <see cref="CompactionPromptBuilder"/> memory extraction prompt generation.
/// Summarization prompts were removed when compaction switched to extractive reduction.
/// </summary>
public class CompactionPromptBuilderTests
{
    [Fact]
    public void BuildMemoryExtractionSystemPrompt_contains_required_sections()
    {
        var prompt = CompactionPromptBuilder.BuildMemoryExtractionSystemPrompt();

        Assert.Contains("Key Facts", prompt);
        Assert.Contains("Action Items", prompt);
        Assert.Contains("Learned Preferences", prompt);
    }

    [Fact]
    public void BuildMemoryExtractionUserPrompt_includes_tool_call_arguments()
    {
        var history = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "System prompt" },
            new()
            {
                Role = ChatRole.Assistant,
                Content = "Let me search",
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = new Netclaw.Tools.ToolCallId("call-1"),
                        Name = new Netclaw.Tools.ToolName("web_search"),
                        ArgumentsJson = """{"query": "netclaw docs"}"""
                    }
                ]
            }
        };

        var prompt = CompactionPromptBuilder.BuildMemoryExtractionUserPrompt(history);

        Assert.DoesNotContain("System prompt", prompt);
        Assert.Contains("""[Called tool: web_search({"query": "netclaw docs"})]""", prompt);
        Assert.Contains("Let me search", prompt);
    }

    [Fact]
    public void BuildMemoryExtractionUserPrompt_empty_history_returns_header_only()
    {
        var prompt = CompactionPromptBuilder.BuildMemoryExtractionUserPrompt([]);

        Assert.Contains("Extract durable memories", prompt);
    }

    [Fact]
    public void BuildMemoryExtractionUserPrompt_renders_rationale_format_for_meta_bearing_tool_calls()
    {
        var meta = new ToolCallMeta { Rationale = "running full test suite" };
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = new Netclaw.Tools.ToolCallId("call-1"),
                        Name = new Netclaw.Tools.ToolName("shell_execute"),
                        ArgumentsJson = """{"Command":"dotnet test"}""",
                        MetaJson = meta.ToJson()
                    }
                ]
            }
        };

        var prompt = CompactionPromptBuilder.BuildMemoryExtractionUserPrompt(history);

        Assert.Contains("→ shell_execute: \"running full test suite\"", prompt);
        Assert.DoesNotContain("[Called tool:", prompt);
    }

    [Fact]
    public void BuildMemoryExtractionUserPrompt_renders_raw_format_for_legacy_tool_calls()
    {
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                ToolCalls =
                [
                    new SerializableToolCall
                    {
                        CallId = new Netclaw.Tools.ToolCallId("call-1"),
                        Name = new Netclaw.Tools.ToolName("web_search"),
                        ArgumentsJson = """{"query":"test"}"""
                    }
                ]
            }
        };

        var prompt = CompactionPromptBuilder.BuildMemoryExtractionUserPrompt(history);

        Assert.Contains("""[Called tool: web_search({"query":"test"})]""", prompt);
        Assert.DoesNotContain("→", prompt);
    }
}
