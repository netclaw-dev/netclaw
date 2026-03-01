using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
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
                {
                    new SerializableToolCall
                    {
                        CallId = "call-1",
                        Name = "web_search",
                        ArgumentsJson = """{"query": "netclaw docs"}"""
                    }
                }
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
}
