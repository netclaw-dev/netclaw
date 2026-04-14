using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Cli.Json;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> presets for the CLI.
/// Use these instead of defining per-command static instances.
/// </summary>
internal static class JsonDefaults
{
    /// <summary>
    /// Daemon API communication: camelCase names, case-insensitive reads, numeric-string handling.
    /// Equivalent to <see cref="JsonSerializerDefaults.Web"/>.
    /// </summary>
    public static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Headless-channel JSON envelope output: camelCase names, nulls omitted.
    /// </summary>
    public static readonly JsonSerializerOptions CliOutput = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Pretty-printed terminal output.
    /// </summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Config and secrets file serialization: pretty-printed with enum values as strings.
    /// </summary>
    public static readonly JsonSerializerOptions ConfigFile = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Config file deserialization: Web defaults (camelCase, case-insensitive) plus enum values as strings.
    /// </summary>
    public static readonly JsonSerializerOptions ConfigRead = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Pretty-printed terminal output with camelCase property names.
    /// Used for structured JSON output (stats, sessions) that maps to daemon API response shapes.
    /// </summary>
    public static readonly JsonSerializerOptions IndentedCamelCase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Deserialization of data containing enum values serialized as strings.
    /// </summary>
    public static readonly JsonSerializerOptions EnumAware = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
