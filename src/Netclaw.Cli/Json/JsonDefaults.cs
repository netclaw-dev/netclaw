// -----------------------------------------------------------------------
// <copyright file="JsonDefaults.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Cli.Json;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> presets for the CLI.
/// Use these instead of defining per-command static instances.
/// </summary>
internal static class JsonDefaults
{
    private static readonly JsonStringEnumConverter EnumConverter = new();

    /// <summary>
    /// Daemon API communication: camelCase names, case-insensitive reads, numeric-string handling.
    /// Equivalent to <see cref="JsonSerializerDefaults.Web"/>.
    /// </summary>
    internal static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Headless-channel JSON envelope output: camelCase names, nulls omitted.
    /// </summary>
    internal static readonly JsonSerializerOptions CliOutput = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Pretty-printed terminal output.
    /// </summary>
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Config and secrets file serialization: pretty-printed with enum values as strings.
    /// </summary>
    internal static readonly JsonSerializerOptions ConfigFile = new()
    {
        WriteIndented = true,
        Converters = { EnumConverter },
    };

    /// <summary>
    /// Config file deserialization: Web defaults (camelCase, case-insensitive) plus enum values as strings.
    /// </summary>
    internal static readonly JsonSerializerOptions ConfigRead = new(JsonSerializerDefaults.Web)
    {
        Converters = { EnumConverter },
    };

    /// <summary>
    /// Pretty-printed terminal output with camelCase property names.
    /// Used for structured JSON output (stats, sessions) that maps to daemon API response shapes.
    /// </summary>
    internal static readonly JsonSerializerOptions IndentedCamelCase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Deserialization of data containing enum values serialized as strings.
    /// </summary>
    internal static readonly JsonSerializerOptions EnumAware = new()
    {
        Converters = { EnumConverter },
    };
}
