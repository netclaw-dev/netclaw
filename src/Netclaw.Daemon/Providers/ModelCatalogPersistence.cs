// -----------------------------------------------------------------------
// <copyright file="ModelCatalogPersistence.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Reads and writes the <c>Models</c> section of <c>netclaw.json</c>.
/// All other keys in the file are preserved verbatim — only the
/// <c>Models</c> sub-tree is mutated.
///
/// Writes are:
/// <list type="bullet">
/// <item>Validated against the embedded JSON schema before touching disk.</item>
/// <item>Atomic — a temp file is written first, then renamed into place.</item>
/// </list>
/// </summary>
public sealed class ModelCatalogPersistence(NetclawPaths paths)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly string[] ValidRoles = ["Main", "Fallback", "Compaction"];

    public ModelCatalogWire.GetSelectionResponse ReadSelection()
    {
        var root = LoadJsonObject(paths.NetclawConfigPath);
        var models = root["Models"] as JsonObject;

        return new ModelCatalogWire.GetSelectionResponse
        {
            Main = ReadReferenceOrDefault(models, "Main"),
            Fallback = ReadReference(models, "Fallback"),
            Compaction = ReadReference(models, "Compaction"),
        };
    }

    /// <summary>
    /// Replaces one model role in <c>netclaw.json</c>.
    /// Returns a validation-error result when the resulting document would
    /// fail schema validation — the file is NOT written in that case.
    /// </summary>
    public ModelCatalogWriteResult Write(ModelCatalogWire.PutSelectionRequest request)
    {
        if (!ValidRoles.Contains(request.Role, StringComparer.Ordinal))
        {
            return ModelCatalogWriteResult.ValidationError(
                $"Role must be one of: {string.Join(", ", ValidRoles)}.",
                []);
        }

        var root = LoadJsonObject(paths.NetclawConfigPath);
        var models = root["Models"] as JsonObject ?? new JsonObject();

        models[request.Role] = BuildReferenceNode(request.Reference);
        root["Models"] = models;
        SeedConfigVersion(root);

        var validationErrors = ValidateAgainstSchema(root);
        if (validationErrors.Length > 0)
            return ModelCatalogWriteResult.ValidationError("Config does not satisfy the JSON schema.", validationErrors);

        WriteConfigAtomic(paths.NetclawConfigPath, root);

        return ModelCatalogWriteResult.Ok(paths.NetclawConfigPath);
    }

    private static ModelCatalogWire.ModelReferenceWire ReadReferenceOrDefault(JsonObject? models, string role)
        => ReadReference(models, role) ?? new ModelCatalogWire.ModelReferenceWire
        {
            Provider = "local-ollama",
            ModelId = "qwen3:30b",
        };

    private static ModelCatalogWire.ModelReferenceWire? ReadReference(JsonObject? models, string role)
    {
        if (models?[role] is not JsonObject refNode)
            return null;

        return new ModelCatalogWire.ModelReferenceWire
        {
            Provider = refNode["Provider"]?.GetValue<string>() ?? string.Empty,
            ModelId = refNode["ModelId"]?.GetValue<string>() ?? string.Empty,
            ContextWindow = refNode["ContextWindow"]?.GetValue<int?>(),
            Provenance = refNode["Provenance"]?.GetValue<string?>(),
            InputModalities = refNode["InputModalities"]?.GetValue<string?>(),
            OutputModalities = refNode["OutputModalities"]?.GetValue<string?>(),
        };
    }

    private static JsonObject BuildReferenceNode(ModelCatalogWire.ModelReferenceWire wire)
    {
        var node = new JsonObject
        {
            ["Provider"] = wire.Provider,
            ["ModelId"] = wire.ModelId,
        };

        if (wire.ContextWindow.HasValue)
            node["ContextWindow"] = wire.ContextWindow.Value;
        if (wire.Provenance is not null)
            node["Provenance"] = wire.Provenance;
        if (wire.InputModalities is not null)
            node["InputModalities"] = wire.InputModalities;
        if (wire.OutputModalities is not null)
            node["OutputModalities"] = wire.OutputModalities;

        return node;
    }

    private static void SeedConfigVersion(JsonObject root)
    {
        if (root["configVersion"] is null)
            root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
    }

    private static string[] ValidateAgainstSchema(JsonObject root)
    {
        var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion);
        if (schemaText is null)
            return [$"No embedded schema found for v{EmbeddedSchemaLoader.CurrentSchemaVersion}."];

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaText);
        }
        catch (Exception ex)
        {
            return [$"Failed to parse embedded schema: {ex.Message}"];
        }

        var evaluation = schema.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid)
            return [];

        return evaluation.Details
            .Where(d => !d.IsValid && d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}"))
            .Take(10)
            .ToArray();
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
}

public sealed record ModelCatalogWriteResult(
    bool Success,
    string? ConfigPath,
    string? ErrorMessage,
    string[] ValidationErrors)
{
    public static ModelCatalogWriteResult Ok(string configPath)
        => new(true, configPath, null, []);

    public static ModelCatalogWriteResult ValidationError(string message, string[] errors)
        => new(false, null, message, errors);
}
