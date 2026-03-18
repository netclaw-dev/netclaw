using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// One-time config migration: rewrite <c>"Type": "openai"</c> + <c>"AuthMethod": "OAuthPkce"</c>
/// to <c>"Type": "openai-codex"</c>. This is needed because OpenAI API keys and Codex OAuth tokens
/// use completely different API surfaces and cannot share a provider type.
/// </summary>
public static class OpenAiCodexConfigMigration
{
    /// <summary>
    /// Scans providers for openai + OAuth entries and rewrites them to openai-codex.
    /// Returns true if any migration was performed and the config file was rewritten.
    /// </summary>
    public static bool MigrateIfNeeded(NetclawPaths paths)
    {
        var configPath = paths.NetclawConfigPath;
        if (!File.Exists(configPath))
            return false;

        try
        {
            var json = File.ReadAllText(configPath);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (root is not JsonObject rootObj)
                return false;

            var providersNode = rootObj["Providers"] as JsonObject;
            if (providersNode is null)
                return false;

            var migrated = false;
            foreach (var (name, providerNode) in providersNode)
            {
                if (providerNode is not JsonObject provider)
                    continue;

                var type = provider["Type"]?.GetValue<string>();
                var authMethod = provider["AuthMethod"]?.GetValue<string>();

                if (string.Equals(type, "openai", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(authMethod, "OAuthPkce", StringComparison.OrdinalIgnoreCase))
                {
                    provider["Type"] = "openai-codex";
                    migrated = true;
                }
            }

            if (!migrated)
                return false;

            File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            return true;
        }
        catch (Exception)
        {
            // Don't crash on migration failure — the old config still works
            return false;
        }
    }
}
