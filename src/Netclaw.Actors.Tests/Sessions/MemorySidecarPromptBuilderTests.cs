using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class MemorySidecarPromptBuilderTests
{
    [Fact]
    public void RecallPlanningPrompt_serializes_request()
    {
        var request = new RecallPlanningRequest(
            "slack/thread",
            "project:slack",
            "automatic",
            "What hotel should I stay in there",
            ["I am speaking at Stir Trek in Ohio"],
            ["We found hotel options near Easton"],
            ["Stir Trek", "Easton", "Ohio"],
            8,
            3);

        var prompt = MemorySidecarPromptBuilder.BuildRecallPlanningUserPrompt(request);
        Assert.Contains("Stir Trek", prompt, StringComparison.Ordinal);
        Assert.Contains("What hotel should I stay in there", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryObservationPrompt_serializes_request()
    {
        var request = new MemoryObservationRequest(
            "slack/thread",
            "turn-1",
            "turn_completed",
            DateTimeOffset.UtcNow,
            new MemoryObservationCurrentTurn(
                "I always fly out of IAH",
                "Understood.",
                ["I always fly out of IAH"],
                []),
            new MemoryObservationRecentContext(
                "User is planning conference travel",
                ["I always fly out of IAH"],
                ["Understood."],
                ["IAH"]),
            new MemoryObservationPolicyScope("project:slack", "normal", false));

        var prompt = MemorySidecarPromptBuilder.BuildMemoryObservationUserPrompt(request);
        Assert.Contains("I always fly out of IAH", prompt, StringComparison.Ordinal);
        Assert.Contains("turn_completed", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryObservationSystemPrompt_constrains_shape_and_allowed_values()
    {
        var prompt = MemorySidecarPromptBuilder.BuildMemoryObservationSystemPrompt();

        Assert.Contains("{ \"proposals\": [ ... ] }", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not invent synonyms", prompt, StringComparison.Ordinal);
        Assert.Contains("upsert_document", prompt, StringComparison.Ordinal);
        Assert.Contains("append_record", prompt, StringComparison.Ordinal);
        Assert.Contains("durable_fact", prompt, StringComparison.Ordinal);
        Assert.Contains("evidence", prompt, StringComparison.Ordinal);
        Assert.Contains("trace", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassificationRules_map_memoryClass_to_operation()
    {
        var rules = MemorySidecarPromptBuilder.BuildClassificationRules();

        Assert.Contains("durable_fact -> operation MUST be \"upsert_document\"", rules, StringComparison.Ordinal);
        Assert.Contains("evidence -> operation MUST be \"append_record\"", rules, StringComparison.Ordinal);
        Assert.Contains("trace -> operation MUST be \"append_record\"", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryObservationSystemPrompt_includes_classification_rules()
    {
        var prompt = MemorySidecarPromptBuilder.BuildMemoryObservationSystemPrompt();

        Assert.Contains("durable_fact -> operation MUST be \"upsert_document\"", prompt, StringComparison.Ordinal);
        Assert.Contains("evidence -> operation MUST be \"append_record\"", prompt, StringComparison.Ordinal);
    }
}
