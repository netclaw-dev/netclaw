// -----------------------------------------------------------------------
// <copyright file="DiscordConfigPersistence.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Reads and writes the <c>Discord</c> section of <c>netclaw.json</c> and the
/// <c>Discord.BotToken</c> entry in <c>secrets.json</c>. Non-Discord keys in
/// either file are preserved verbatim — this service only mutates the
/// <c>Discord</c> sub-tree.
///
/// Changes take effect on the next daemon restart: <see cref="DiscordChannelOptions"/>
/// is bound once during host construction.
/// </summary>
public sealed class DiscordConfigPersistence(NetclawPaths paths, ISecretsProtector? protector = null)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public GetDiscordConfigResponse Read()
    {
        var configRoot = LoadJsonObject(paths.NetclawConfigPath);
        var secretsRoot = LoadJsonObject(paths.SecretsPath);

        var discord = configRoot["Discord"] as JsonObject;
        var secretsDiscord = secretsRoot["Discord"] as JsonObject;

        var tokenValue = secretsDiscord?["BotToken"]?.GetValue<string>();

        return new GetDiscordConfigResponse
        {
            Enabled = discord?["Enabled"]?.GetValue<bool>() ?? false,
            BotTokenIsSet = !string.IsNullOrWhiteSpace(tokenValue),
            DefaultChannelId = discord?["DefaultChannelId"]?.GetValue<string>(),
            AllowDirectMessages = discord?["AllowDirectMessages"]?.GetValue<bool>() ?? false,
            MentionOnly = discord?["MentionOnly"]?.GetValue<bool>() ?? true,
            MentionRequiredInDm = discord?["MentionRequiredInDm"]?.GetValue<bool>() ?? false,
            AllowedChannelIds = ReadStringArray(discord, "AllowedChannelIds"),
            AllowedUserIds = ReadStringArray(discord, "AllowedUserIds"),
            ChannelAudiences = ReadStringMap(discord, "ChannelAudiences"),
        };
    }

    public PutDiscordConfigResponse Write(PutDiscordConfigRequest request)
    {
        // ---- netclaw.json ----
        var configRoot = LoadJsonObject(paths.NetclawConfigPath);
        var discord = configRoot["Discord"] as JsonObject ?? new JsonObject();

        discord["Enabled"] = request.Enabled;
        discord["AllowDirectMessages"] = request.AllowDirectMessages;
        discord["MentionOnly"] = request.MentionOnly;
        discord["MentionRequiredInDm"] = request.MentionRequiredInDm;

        if (string.IsNullOrWhiteSpace(request.DefaultChannelId))
            discord.Remove("DefaultChannelId");
        else
            discord["DefaultChannelId"] = request.DefaultChannelId;

        WriteStringArray(discord, "AllowedChannelIds", request.AllowedChannelIds);
        WriteStringArray(discord, "AllowedUserIds", request.AllowedUserIds);
        WriteStringMap(discord, "ChannelAudiences", request.ChannelAudiences);

        // ---- secrets.json (only touched when the token field changed) ----
        if (request.BotToken is not null)
        {
            var secretsRoot = LoadJsonObject(paths.SecretsPath);
            var secretsDiscord = secretsRoot["Discord"] as JsonObject ?? new JsonObject();

            if (string.IsNullOrEmpty(request.BotToken))
                secretsDiscord.Remove("BotToken");
            else
                secretsDiscord["BotToken"] = request.BotToken;

            if (secretsDiscord.Count == 0)
                secretsRoot.Remove("Discord");
            else
                secretsRoot["Discord"] = secretsDiscord;

            var json = secretsRoot.ToJsonString(WriteOptions);
            SecretsFileWriter.Write(paths.SecretsPath, json, protector);
        }

        // write discord config _after_ secrets, since the daemon watcher
        // does not watch for changes in the secrets config
        configRoot["Discord"] = discord;
        WriteConfigAtomic(paths.NetclawConfigPath, configRoot);

        return new PutDiscordConfigResponse
        {
            ConfigPath = paths.NetclawConfigPath,
            SecretsPath = paths.SecretsPath,
            RestartRequired = true,
        };
    }

    private static JsonObject LoadJsonObject(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        var node = JsonNode.Parse(text);
        return node as JsonObject ?? new JsonObject();
    }

    private static void WriteConfigAtomic(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = root.ToJsonString(WriteOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path))
            File.Replace(temp, path, destinationBackupFileName: null);
        else
            File.Move(temp, path);
    }

    private static string[] ReadStringArray(JsonObject? container, string key)
    {
        if (container?[key] is not JsonArray array)
            return [];

        return array
            .OfType<JsonValue>()
            .Select(v => v.GetValue<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    private static Dictionary<string, string> ReadStringMap(JsonObject? container, string key)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (container?[key] is not JsonObject map)
            return result;

        foreach (var kvp in map)
        {
            if (kvp.Value is JsonValue value)
                result[kvp.Key] = value.GetValue<string>();
        }

        return result;
    }

    private static void WriteStringArray(JsonObject container, string key, string[] values)
    {
        var cleaned = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (cleaned.Length == 0)
        {
            container.Remove(key);
            return;
        }

        var array = new JsonArray();
        foreach (var v in cleaned)
            array.Add(v);
        container[key] = array;
    }

    private static void WriteStringMap(JsonObject container, string key, Dictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            container.Remove(key);
            return;
        }

        var map = new JsonObject();
        foreach (var kvp in values)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                continue;
            map[kvp.Key] = kvp.Value;
        }

        if (map.Count == 0)
            container.Remove(key);
        else
            container[key] = map;
    }
}
