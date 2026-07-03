// -----------------------------------------------------------------------
// <copyright file="DoctorJsonConfigReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

internal static class DoctorJsonConfigReader
{
    /// <summary>
    /// Reads a boolean property from a <see cref="JsonObject"/>. Returns <c>false</c>
    /// if the property is missing, not a boolean, or not <c>true</c>.
    /// </summary>
    public static bool ReadBool(JsonObject obj, string property)
        => obj[property] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// Reads a string array property from a <see cref="JsonObject"/>. Returns an empty
    /// list if the property is missing or not an array.
    /// </summary>
    public static List<string> ReadStringArray(JsonObject obj, string property)
    {
        if (obj[property] is not JsonArray arr)
            return [];

        return [.. arr.Select(v => v?.GetValue<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()];
    }

    /// <summary>
    /// True when NETCLAW_-prefixed configuration-binding environment variables
    /// are present (double-underscore section keys, e.g.
    /// <c>NETCLAW_Models__Main__ModelId</c>). Plain control variables like
    /// <c>NETCLAW_HOME</c> or <c>NETCLAW_DAEMON_ENDPOINT</c> do not count —
    /// they configure path/endpoint resolution, not the daemon itself.
    /// </summary>
    public static bool HasEnvironmentConfig(System.Collections.IDictionary? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables();
        foreach (var key in environment.Keys)
        {
            if (key is string name
                && name.StartsWith("NETCLAW_", StringComparison.Ordinal)
                && name.Contains("__", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static (JsonObject? Root, DoctorCheckResult? Error) TryReadConfig(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath))
        {
            // An env-only instance is configured, just not via this file — a
            // "not configured, run init" warning here is a misdiagnosis (the
            // daemon binds NETCLAW_ env vars over an optional JSON file).
            // File-content checks are legitimately skipped; say so truthfully.
            if (HasEnvironmentConfig())
            {
                return (null, DoctorCheckResult.Pass(
                    "Config File",
                    "No netclaw.json; NETCLAW_ environment configuration detected — file-based checks skipped."));
            }

            return (null, DoctorCheckResult.Warning(
                "Config File",
                CliConfigPreflight.MissingConfigMessage,
                $"Run `netclaw init` to create {paths.NetclawConfigPath}."));
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath)) as JsonObject;
            if (root is null)
            {
                return (null, DoctorCheckResult.Error(
                    "Config File",
                    "netclaw.json root must be a JSON object.",
                    "Wrap config in a top-level JSON object."));
            }

            return (root, null);
        }
        catch (Exception ex)
        {
            return (null, DoctorCheckResult.Error(
                "Config File",
                $"Failed parsing {paths.NetclawConfigPath}: {ex.Message}",
                "Fix malformed JSON in netclaw.json."));
        }
    }
}
