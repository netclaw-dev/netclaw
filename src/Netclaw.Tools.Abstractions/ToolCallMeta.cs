using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Tools;

/// <summary>
/// Per-call metadata envelope extracted from tool call arguments before dispatch.
/// Injected into tool schemas as <c>_rationale</c>, <c>_timeout_seconds</c>,
/// and <c>_background</c>; persisted as opaque JSON on
/// <c>SerializableToolCall.MetaJson</c>.
/// </summary>
public sealed record ToolCallMeta
{
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutHintSeconds { get; init; }

    [JsonPropertyName("background")]
    public bool Background { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ToolCallMeta? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ToolCallMeta>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
