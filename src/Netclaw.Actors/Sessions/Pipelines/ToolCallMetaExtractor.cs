using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Extracts <see cref="ToolCallMeta"/> fields from a <see cref="FunctionCallContent"/>
/// and returns a cleaned tool call with meta keys removed.
/// </summary>
internal static class ToolCallMetaExtractor
{
    public static (ToolCallMeta? Meta, FunctionCallContent CleanedToolCall) Extract(FunctionCallContent tc)
    {
        var (meta, cleanArgs) = ToolCallMeta.ExtractFrom(tc.Arguments);
        if (meta is null)
            return (null, tc);

        var cleanedTc = new FunctionCallContent(tc.CallId, tc.Name, cleanArgs);
        return (meta, cleanedTc);
    }

    /// <summary>
    /// Computes the effective timeout by clamping the LLM's hint between the tool's
    /// default floor and the config ceiling.
    /// </summary>
    public static TimeSpan ComputeEffectiveTimeout(
        int? hintSeconds, TimeSpan defaultTimeout, int maxToolTimeoutSeconds)
    {
        if (!hintSeconds.HasValue || hintSeconds.Value <= 0)
            return defaultTimeout;

        var floorSeconds = (int)defaultTimeout.TotalSeconds;
        if (hintSeconds.Value < floorSeconds)
            return defaultTimeout;

        var clamped = Math.Min(hintSeconds.Value, maxToolTimeoutSeconds);
        return TimeSpan.FromSeconds(clamped);
    }
}
