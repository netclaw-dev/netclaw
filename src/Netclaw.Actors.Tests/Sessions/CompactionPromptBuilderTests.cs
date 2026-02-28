using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tests for <see cref="CompactionPromptBuilder"/> structured prompt generation.
/// </summary>
public class CompactionPromptBuilderTests
{
    [Fact]
    public void BuildSummarizationSystemPrompt_contains_key_sections()
    {
        var prompt = CompactionPromptBuilder.BuildSummarizationSystemPrompt();

        Assert.Contains("compaction agent", prompt);
        Assert.Contains("Goal", prompt);
        Assert.Contains("Key Facts", prompt);
        Assert.Contains("past tense", prompt);
    }

    [Fact]
    public void BuildSummarizationUserPrompt_skips_system_messages()
    {
        var history = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "You are helpful." },
            new() { Role = ChatRole.User, Content = "Hello" },
            new() { Role = ChatRole.Assistant, Content = "Hi there!" }
        };

        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt(history);

        Assert.DoesNotContain("You are helpful", prompt);
        Assert.Contains("Hello", prompt);
        Assert.Contains("Hi there!", prompt);
    }

    [Fact]
    public void BuildSummarizationUserPrompt_includes_tool_call_arguments()
    {
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                {
                    new SerializableToolCall
                    {
                        CallId = "call-1",
                        Name = "web_search",
                        ArgumentsJson = """{"query": "freshdesk ticket 579"}"""
                    }
                }
            },
            new()
            {
                Role = ChatRole.Tool,
                Content = "Found 3 results",
                ToolCallId = "call-1",
                Name = "web_search"
            }
        };

        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt(history);

        Assert.Contains("""[Called tool: web_search({"query": "freshdesk ticket 579"})]""", prompt);
        Assert.Contains("Found 3 results", prompt);
    }

    [Fact]
    public void BuildSummarizationUserPrompt_tool_call_with_no_args_omits_parens()
    {
        var history = new List<SerializableChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                ToolCalls =
                {
                    new SerializableToolCall
                    {
                        CallId = "call-1",
                        Name = "list_files",
                        ArgumentsJson = ""
                    }
                }
            }
        };

        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt(history);

        Assert.Contains("[Called tool: list_files]", prompt);
        Assert.DoesNotContain("()", prompt);
    }

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
    public void BuildSummarizationUserPrompt_empty_history_returns_header_only()
    {
        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt([]);

        Assert.Contains("Summarize the following", prompt);
    }
}
