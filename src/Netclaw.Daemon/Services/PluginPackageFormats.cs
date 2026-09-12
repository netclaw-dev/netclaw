// -----------------------------------------------------------------------
// <copyright file="PluginPackageFormats.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

internal sealed record PluginPackageDescriptor(
    string Format,
    string Name,
    string? Version,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> Notices,
    bool SkipInvalidSkills);

internal interface IPluginPackageAdapter
{
    string Format { get; }

    PluginPackageDescriptor Parse(byte[] manifest, bool hasMcpConfig, string commit);
}

internal static class PluginPackageSelector
{
    private const string AgentPluginSchema =
        "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";

    private static readonly IPluginPackageAdapter AgentPluginAdapter = new AgentPluginPackageAdapter();
    private static readonly IPluginPackageAdapter CodexAdapter = new CodexPluginPackageAdapter();

    public static PluginPackageDescriptor Select(
        string requestedFormat,
        byte[]? agentPluginManifest,
        byte[]? codexManifest,
        bool hasMcpConfig,
        string commit)
    {
        return requestedFormat switch
        {
            ManagedPluginSourceValidator.AgentPluginFormat => ParseRequired(
                AgentPluginAdapter, agentPluginManifest, hasMcpConfig, commit),
            ManagedPluginSourceValidator.CodexFormat => ParseRequired(
                CodexAdapter, codexManifest, hasMcpConfig, commit),
            ManagedPluginSourceValidator.AutoFormat => SelectAutomatic(
                agentPluginManifest, codexManifest, hasMcpConfig, commit),
            _ => throw new InvalidOperationException("The plugin format is not supported."),
        };
    }

    private static PluginPackageDescriptor SelectAutomatic(
        byte[]? agentPluginManifest,
        byte[]? codexManifest,
        bool hasMcpConfig,
        string commit)
    {
        if (HasRecognizedAgentPluginSchema(agentPluginManifest))
            return AgentPluginAdapter.Parse(agentPluginManifest!, hasMcpConfig, commit);
        return ParseRequired(CodexAdapter, codexManifest, hasMcpConfig, commit);
    }

    private static PluginPackageDescriptor ParseRequired(
        IPluginPackageAdapter adapter,
        byte[]? manifest,
        bool hasMcpConfig,
        string commit)
    {
        if (manifest is null)
            throw new GitSkillPluginRejectedException(commit, $"The {adapter.Format} plugin manifest is missing.");
        return adapter.Parse(manifest, hasMcpConfig, commit);
    }

    private static bool HasRecognizedAgentPluginSchema(byte[]? manifest)
    {
        if (manifest is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(
                Encoding.UTF8.GetString(manifest),
                new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("$schema", out var schema)
                && schema.ValueKind == JsonValueKind.String
                && string.Equals(schema.GetString(), AgentPluginSchema, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class AgentPluginPackageAdapter : IPluginPackageAdapter
    {
        private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
        {
            "$schema", "name", "version", "description", "author", "homepage",
            "repository", "license", "keywords", "extensions",
        };

        public string Format => ManagedPluginSourceValidator.AgentPluginFormat;

        public PluginPackageDescriptor Parse(byte[] manifest, bool hasMcpConfig, string commit)
        {
            try
            {
                using var document = ParseDocument(manifest);
                var root = document.RootElement;
                RequireObject(root, "The Agent Plugins manifest must be an object.");
                var schema = RequireString(root, "$schema", allowEmpty: false);
                if (!string.Equals(schema, AgentPluginSchema, StringComparison.Ordinal))
                    throw new InvalidDataException("The Agent Plugins manifest schema is not supported.");
                var name = RequireString(root, "name", allowEmpty: false);
                if (!IsPortableName(name))
                    throw new InvalidDataException("The Agent Plugins manifest name is invalid.");

                var version = OptionalString(root, "version");
                ValidateOptionalString(root, "description");
                ValidateOptionalString(root, "homepage");
                ValidateOptionalString(root, "repository");
                ValidateOptionalString(root, "license");
                ValidateAuthor(root);
                ValidateKeywords(root);

                var notices = root.EnumerateObject()
                    .Where(property => !KnownFields.Contains(property.Name))
                    .Select(property => $"The importer ignored the unknown '{SafeFieldName(property.Name)}' manifest field.")
                    .ToList();
                ValidateExtensions(root, notices);
                if (hasMcpConfig)
                    notices.Add("The importer ignored the unsupported MCP plugin component.");

                return new PluginPackageDescriptor(
                    Format,
                    name,
                    version,
                    ["./skills/"],
                    notices,
                    SkipInvalidSkills: true);
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidDataException)
            {
                throw new GitSkillPluginRejectedException(commit, ex.Message);
            }
        }

        private static string SafeFieldName(string value)
        {
            const int maximumLength = 80;
            var safe = new string(value.Select(
                static character => char.IsControl(character)
                    || char.GetUnicodeCategory(character) == UnicodeCategory.Format ? '?' : character).ToArray());
            return safe.Length <= maximumLength ? safe : safe[..maximumLength];
        }

        private static bool IsPortableName(string value)
        {
            if (value.Length is 0 or > 64 || value[0] is '-' or '.' || value[^1] is '-' or '.')
                return false;
            if (value.Contains("--", StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal))
                return false;
            return value.All(static character => char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character) || character is '-' or '.');
        }

        private static void ValidateAuthor(JsonElement root)
        {
            if (!root.TryGetProperty("author", out var author))
                return;
            RequireObject(author, "The Agent Plugins author must be an object.");
            foreach (var property in author.EnumerateObject())
            {
                if (property.Name is not ("name" or "email" or "url")
                    || property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("The Agent Plugins author is invalid.");
                }
            }
        }

        private static void ValidateKeywords(JsonElement root)
        {
            if (!root.TryGetProperty("keywords", out var keywords))
                return;
            if (keywords.ValueKind != JsonValueKind.Array
                || keywords.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
            {
                throw new InvalidDataException("The Agent Plugins keywords must contain only strings.");
            }
        }

        private static void ValidateExtensions(JsonElement root, List<string> notices)
        {
            if (!root.TryGetProperty("extensions", out var extensions))
                return;
            if (extensions.ValueKind != JsonValueKind.Object)
            {
                notices.Add("The importer ignored the invalid Agent Plugins extensions field.");
            }
        }
    }

    private sealed class CodexPluginPackageAdapter : IPluginPackageAdapter
    {
        private static readonly HashSet<string> ExecutableComponentNames = new(StringComparer.Ordinal)
        {
            "agents", "apps", "commands", "entrypoint", "hooks", "installers", "lsp",
            "mcp", "mcpServers", "scripts", "servers",
        };

        public string Format => ManagedPluginSourceValidator.CodexFormat;

        public PluginPackageDescriptor Parse(byte[] manifest, bool hasMcpConfig, string commit)
        {
            try
            {
                using var document = ParseDocument(manifest);
                var root = document.RootElement;
                RequireObject(root, "The Codex manifest must be an object.");
                var name = RequireString(root, "name", allowEmpty: false);
                if (!ManagedPluginSourceValidator.TryValidateCodexPackageName(name, out var nameError))
                    throw new InvalidDataException($"The Codex manifest name is invalid: {nameError}");

                string? version = null;
                if (root.TryGetProperty("version", out var versionElement))
                {
                    if (versionElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(versionElement.GetString())
                        || !IsStrictVersion(versionElement.GetString()!))
                    {
                        throw new InvalidDataException("The Codex plugin version must use strict SemVer form.");
                    }
                    version = versionElement.GetString();
                }

                var skillPaths = !root.TryGetProperty("skills", out var skills)
                    ? new[] { "./skills/" }
                    : skills.ValueKind == JsonValueKind.String
                        ? new[] { "./skills/", skills.GetString()! }
                        : throw new InvalidDataException("The Codex skills field must be one string.");
                var notices = root.EnumerateObject()
                    .Where(property => ExecutableComponentNames.Contains(property.Name))
                    .Select(property => $"The importer ignored the '{property.Name}' plugin component.")
                    .Order(StringComparer.Ordinal)
                    .ToList();
                if (hasMcpConfig)
                    notices.Add("The importer ignored the unsupported MCP plugin component.");

                return new PluginPackageDescriptor(
                    Format,
                    name,
                    version,
                    skillPaths.Distinct(StringComparer.Ordinal).ToArray(),
                    notices,
                    SkipInvalidSkills: false);
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidDataException)
            {
                throw new GitSkillPluginRejectedException(commit, ex.Message);
            }
        }
    }

    private static JsonDocument ParseDocument(byte[] manifest)
        => JsonDocument.Parse(
            new UTF8Encoding(false, true).GetString(manifest),
            new JsonDocumentOptions { MaxDepth = 32 });

    private static void RequireObject(JsonElement value, string error)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(error);
    }

    private static string RequireString(JsonElement root, string propertyName, bool allowEmpty)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || (!allowEmpty && string.IsNullOrEmpty(value.GetString())))
        {
            throw new InvalidDataException($"The plugin manifest requires a valid {propertyName} value.");
        }
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The Agent Plugins {propertyName} field must be a string.");
        return value.GetString();
    }

    private static void ValidateOptionalString(JsonElement root, string propertyName)
        => OptionalString(root, propertyName);

    private static bool IsStrictVersion(string value)
    {
        var plusIndex = value.IndexOf('+', StringComparison.Ordinal);
        var coreAndPrerelease = plusIndex < 0 ? value : value[..plusIndex];
        var build = plusIndex < 0 ? null : value[(plusIndex + 1)..];
        if (plusIndex >= 0 && (build!.Length == 0 || !ValidIdentifiers(build, numericLeadingZeroAllowed: true)))
            return false;
        var prereleaseIndex = coreAndPrerelease.IndexOf('-', StringComparison.Ordinal);
        var coreValue = prereleaseIndex < 0 ? coreAndPrerelease : coreAndPrerelease[..prereleaseIndex];
        var prerelease = prereleaseIndex < 0 ? null : coreAndPrerelease[(prereleaseIndex + 1)..];
        if (prereleaseIndex >= 0 && (prerelease!.Length == 0
            || !ValidIdentifiers(prerelease, numericLeadingZeroAllowed: false)))
        {
            return false;
        }
        var core = coreValue.Split('.');
        return core.Length == 3 && core.All(ValidCoreNumber);
    }

    private static bool ValidCoreNumber(string value)
        => value.Length > 0 && value.All(char.IsAsciiDigit)
            && (value.Length == 1 || value[0] != '0');

    private static bool ValidIdentifiers(string value, bool numericLeadingZeroAllowed)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0
                || !identifier.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                return false;
            }
            if (!numericLeadingZeroAllowed && identifier.All(char.IsAsciiDigit)
                && identifier.Length > 1 && identifier[0] == '0')
            {
                return false;
            }
        }
        return true;
    }
}
