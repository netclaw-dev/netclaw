using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tests for <see cref="CompactionPromptBuilder"/> structured prompt generation
/// and output parsing.
/// </summary>
public class CompactionPromptBuilderTests
{
    [Fact]
    public void BuildSummarizationSystemPrompt_contains_structured_output_markers()
    {
        var prompt = CompactionPromptBuilder.BuildSummarizationSystemPrompt();

        Assert.Contains("SUMMARY", prompt);
        Assert.Contains("PRESERVE_FROM_INDEX", prompt);
        Assert.Contains("compaction agent", prompt);
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
    public void BuildSummarizationUserPrompt_includes_0_based_indices()
    {
        var history = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "System prompt" },
            new() { Role = ChatRole.User, Content = "First" },
            new() { Role = ChatRole.Assistant, Content = "Second" },
            new() { Role = ChatRole.User, Content = "Third" }
        };

        var prompt = CompactionPromptBuilder.BuildSummarizationUserPrompt(history);

        Assert.Contains("[0]", prompt);
        Assert.Contains("[1]", prompt);
        Assert.Contains("[2]", prompt);
        // System prompt is not indexed
        Assert.DoesNotContain("System prompt", prompt);
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

        Assert.Contains("Analyze the following", prompt);
    }

    // ── ParseCompactionOutput tests ──

    [Fact]
    public void ParseCompactionOutput_valid_structured_output()
    {
        var input = """
            ## SUMMARY
            The user was working on a Freshdesk ticket investigation.
            They searched for ticket #579 and found it was a permissions issue.

            **Goal**: Resolve Freshdesk ticket 579
            **Completed**: Identified root cause as missing ACL entry
            **Key Facts**: Ticket #579, customer: Acme Corp

            ## PRESERVE_FROM_INDEX
            5
            """;

        var (summary, index) = CompactionPromptBuilder.ParseCompactionOutput(input);

        Assert.Contains("Freshdesk ticket investigation", summary);
        Assert.Contains("Acme Corp", summary);
        Assert.Equal(5, index);
    }

    [Fact]
    public void ParseCompactionOutput_with_angle_brackets_around_index()
    {
        var input = """
            ## SUMMARY
            Summary content here.

            ## PRESERVE_FROM_INDEX
            <12>
            """;

        var (summary, index) = CompactionPromptBuilder.ParseCompactionOutput(input);

        Assert.Contains("Summary content here", summary);
        Assert.Equal(12, index);
    }

    [Fact]
    public void ParseCompactionOutput_missing_preserve_section_returns_negative_one()
    {
        var input = """
            ## SUMMARY
            The user was debugging a build failure.
            """;

        var (summary, index) = CompactionPromptBuilder.ParseCompactionOutput(input);

        Assert.Contains("debugging a build failure", summary);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void ParseCompactionOutput_missing_summary_section_returns_full_response()
    {
        var input = "This is just plain text with no structure.";

        var (summary, index) = CompactionPromptBuilder.ParseCompactionOutput(input);

        Assert.Equal("This is just plain text with no structure.", summary);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void ParseCompactionOutput_empty_input_returns_empty_summary()
    {
        var (summary, index) = CompactionPromptBuilder.ParseCompactionOutput("");

        Assert.Equal(string.Empty, summary);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void ParseCompactionOutput_invalid_index_value_returns_negative_one()
    {
        var input = """
            ## SUMMARY
            Some summary.

            ## PRESERVE_FROM_INDEX
            not-a-number
            """;

        var (summary, index) = CompactionPromptBuilder.ParseCompactionOutput(input);

        Assert.Contains("Some summary", summary);
        Assert.Equal(-1, index);
    }
}
