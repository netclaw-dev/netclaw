using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class ObservationPromptBuilderTests
{
    [Fact]
    public void System_prompt_instructs_compression()
    {
        var prompt = ObservationPromptBuilder.BuildObservationSystemPrompt();

        Assert.Contains("observation", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compress", prompt, StringComparison.OrdinalIgnoreCase);
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
    public void User_prompt_truncates_long_tool_results()
    {
        var longContent = new string('x', 1000);
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.Tool, Content = longContent, Name = "shell_execute", ToolCallId = "1" },
        };

        var prompt = ObservationPromptBuilder.BuildObservationUserPrompt(messages);

        // Should be truncated to 500 chars
        Assert.True(prompt.Length < longContent.Length);
        Assert.Contains("...", prompt);
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
    public void WrapObservations_adds_delimiter_when_missing()
    {
        var text = "- Discussed deployment\n- User prefers Docker";

        var wrapped = ObservationPromptBuilder.WrapObservations(text);

        Assert.StartsWith("[observations from earlier in this session]", wrapped);
    }

    [Fact]
    public void WrapObservations_preserves_existing_delimiter()
    {
        var text = "[observations from earlier in this session]\n- Already formatted";

        var wrapped = ObservationPromptBuilder.WrapObservations(text);

        Assert.Equal(text, wrapped);
    }
}
