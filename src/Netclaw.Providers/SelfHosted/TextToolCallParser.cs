using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Extracts structured tool calls from LLM text responses when the model emits
/// tool calls as text (e.g. Qwen3.5's XML-like format) instead of using the
/// OpenAI-structured tool_calls response field.
/// </summary>
internal static partial class TextToolCallParser
{
    /// <summary>
    /// Attempts to extract tool calls from a text string.
    /// Returns an empty list if no text-based tool calls are found.
    /// </summary>
    public static List<FunctionCallContent> ExtractFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var results = new List<FunctionCallContent>();

        foreach (Match block in ToolCallBlockRegex().Matches(text))
        {
            var functionMatch = FunctionNameRegex().Match(block.Value);
            if (!functionMatch.Success)
                continue;

            var functionName = functionMatch.Groups[1].Value;
            var arguments = new Dictionary<string, object?>();

            foreach (Match param in ParameterRegex().Matches(block.Value))
            {
                arguments[param.Groups[1].Value] = param.Groups[2].Value.Trim();
            }

            results.Add(new FunctionCallContent(
                Guid.NewGuid().ToString("N"),
                functionName,
                arguments));
        }

        return results;
    }

    /// <summary>
    /// Removes text-based tool call blocks from the text, returning
    /// the remaining content (if any).
    /// </summary>
    public static string StripToolCallText(string text)
    {
        return ToolCallBlockRegex().Replace(text, "").Trim();
    }

    // Matches the entire <tool_call>...</tool_call> block
    [GeneratedRegex(@"<tool_call>\s*<function=([^>]+)>\s*(.*?)\s*</function>\s*</tool_call>",
        RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex ToolCallBlockRegex();

    // Extracts the function name from <function=NAME>
    [GeneratedRegex(@"<function=([^>]+)>", RegexOptions.Compiled)]
    private static partial Regex FunctionNameRegex();

    // Extracts parameter name and value from <parameter=KEY>VALUE</parameter>
    [GeneratedRegex(@"<parameter=([^>]+)>(.*?)</parameter>",
        RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex ParameterRegex();
}
