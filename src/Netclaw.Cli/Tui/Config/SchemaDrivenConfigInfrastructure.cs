// -----------------------------------------------------------------------
// <copyright file="SchemaDrivenConfigInfrastructure.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Config;

internal enum ConfigFieldStorage
{
    ConfigFile,
    SecretsFile,
}

internal enum ConfigFieldWidget
{
    EnumSelection,
    TextInput,
    PasswordInput,
}

internal enum ConfigFieldValueKind
{
    String,
    Boolean,
}

internal enum ConfigValidationSeverity
{
    Error,
    Warning,
}

internal enum ConfigStatusTone
{
    Neutral,
    Success,
    Warning,
    Error,
}

internal sealed record ConfigStatusMessage(string Text, ConfigStatusTone Tone);

internal sealed record ConfigValidationIssue(string? Path, ConfigValidationSeverity Severity, string Message);

internal sealed record ConfigValidationSummary(IReadOnlyList<ConfigValidationIssue> Issues)
{
    public static readonly ConfigValidationSummary Empty = new([]);

    public bool HasErrors => Issues.Any(static i => i.Severity == ConfigValidationSeverity.Error);

    public bool HasWarnings => Issues.Any(static i => i.Severity == ConfigValidationSeverity.Warning);

    public bool HasIssues => Issues.Count > 0;

    public IReadOnlyList<ConfigValidationIssue> IssuesFor(string path)
        => [.. Issues.Where(i => string.Equals(i.Path, path, StringComparison.Ordinal))];
}

internal sealed record ConfigEnumOption(string Value, string Label);

internal sealed record ConfigFieldMetadata(
    bool IncludeInEditor = true,
    string? Label = null,
    ConfigFieldStorage Storage = ConfigFieldStorage.ConfigFile,
    ConfigFieldWidget? Widget = null,
    string? Placeholder = null,
    string? Hint = null,
    string? ApplicableWhenPath = null,
    string? ApplicableWhenEquals = null,
    string? InactiveText = null,
    bool PreserveBlankSecret = true,
    bool TrimDefaultOnSave = false,
    IReadOnlyDictionary<string, string>? OptionLabels = null);

internal sealed record ProjectedConfigField(
    string Path,
    string PropertyName,
    string Label,
    string? Description,
    ConfigFieldValueKind ValueKind,
    ConfigFieldStorage Storage,
    ConfigFieldWidget Widget,
    bool Nullable,
    object? DefaultValue,
    bool TrimDefaultOnSave,
    bool PreserveBlankSecret,
    string? Placeholder,
    string? Hint,
    string? ApplicableWhenPath,
    string? ApplicableWhenEquals,
    string? InactiveText,
    IReadOnlyList<ConfigEnumOption> EnumOptions);

internal static class SearchConfigMetadata
{
    public static IReadOnlyDictionary<string, ConfigFieldMetadata> Fields { get; } =
        new Dictionary<string, ConfigFieldMetadata>(StringComparer.Ordinal)
        {
            ["Search.Enabled"] = new(IncludeInEditor: false),
            ["Search.Backend"] = new(
                Label: "Backend",
                Widget: ConfigFieldWidget.EnumSelection,
                Hint: "Select the search backend Netclaw should use for web search and URL fetch augmentation.",
                TrimDefaultOnSave: true,
                OptionLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["duckduckgo"] = "DuckDuckGo",
                    ["brave"] = "Brave",
                    ["searxng"] = "SearXng (self-hosted)",
                }),
            ["Search.BraveApiKey"] = new(
                Label: "Brave API key",
                Storage: ConfigFieldStorage.SecretsFile,
                Widget: ConfigFieldWidget.PasswordInput,
                Placeholder: "Enter Brave Search API key...",
                Hint: "Stored in secrets.json. Leave blank to keep the existing key.",
                ApplicableWhenPath: "Search.Backend",
                ApplicableWhenEquals: "brave",
                InactiveText: "(not applicable - only required for Brave)",
                PreserveBlankSecret: true),
            ["Search.SearXngEndpoint"] = new(
                Label: "SearXng instance URL",
                Widget: ConfigFieldWidget.TextInput,
                Placeholder: "https://search.example.com",
                Hint: "Enter the base URL of your SearXNG instance. JSON format must be enabled in settings.yml.",
                ApplicableWhenPath: "Search.Backend",
                ApplicableWhenEquals: "searxng",
                InactiveText: "(not applicable - only required for SearXng)",
                TrimDefaultOnSave: true),
        };
}

internal sealed class ConfigSectionSchemaProjector
{
    private readonly JsonObject _schemaRoot;

    public ConfigSectionSchemaProjector()
    {
        var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion)
            ?? throw new InvalidOperationException(
                $"Missing embedded netclaw config schema v{EmbeddedSchemaLoader.CurrentSchemaVersion}.");

        _schemaRoot = JsonNode.Parse(schemaText) as JsonObject
            ?? throw new InvalidOperationException("Embedded netclaw config schema is not a JSON object.");
    }

    public IReadOnlyList<ProjectedConfigField> ProjectTopLevelSection(
        string sectionName,
        IReadOnlyDictionary<string, ConfigFieldMetadata> metadata)
    {
        if (_schemaRoot["properties"] is not JsonObject rootProperties
            || rootProperties[sectionName] is not JsonObject sectionSchema
            || sectionSchema["properties"] is not JsonObject sectionProperties)
        {
            throw new InvalidOperationException($"Section '{sectionName}' was not found in the embedded config schema.");
        }

        var fields = new List<ProjectedConfigField>();
        foreach (var (propertyName, propertyNode) in sectionProperties)
        {
            if (propertyNode is not JsonObject propertySchema)
                continue;

            var path = $"{sectionName}.{propertyName}";
            var fieldMetadata = metadata.TryGetValue(path, out var declared) ? declared : new ConfigFieldMetadata();
            if (!fieldMetadata.IncludeInEditor)
                continue;

            var enumOptions = ReadEnumOptions(propertySchema, fieldMetadata);
            var (valueKind, nullable) = ReadValueKind(propertySchema, enumOptions.Count > 0);
            var defaultValue = ReadScalar(propertySchema["default"]);
            var widget = fieldMetadata.Widget
                ?? (enumOptions.Count > 0 ? ConfigFieldWidget.EnumSelection : ConfigFieldWidget.TextInput);

            fields.Add(new ProjectedConfigField(
                Path: path,
                PropertyName: propertyName,
                Label: fieldMetadata.Label ?? ToDisplayLabel(propertyName),
                Description: propertySchema["description"]?.GetValue<string>(),
                ValueKind: valueKind,
                Storage: fieldMetadata.Storage,
                Widget: widget,
                Nullable: nullable,
                DefaultValue: defaultValue,
                TrimDefaultOnSave: fieldMetadata.TrimDefaultOnSave,
                PreserveBlankSecret: fieldMetadata.PreserveBlankSecret,
                Placeholder: fieldMetadata.Placeholder,
                Hint: fieldMetadata.Hint,
                ApplicableWhenPath: fieldMetadata.ApplicableWhenPath,
                ApplicableWhenEquals: fieldMetadata.ApplicableWhenEquals,
                InactiveText: fieldMetadata.InactiveText,
                EnumOptions: enumOptions));
        }

        return fields;
    }

    private static IReadOnlyList<ConfigEnumOption> ReadEnumOptions(JsonObject propertySchema, ConfigFieldMetadata metadata)
    {
        if (propertySchema["enum"] is not JsonArray enumArray)
            return [];

        var options = new List<ConfigEnumOption>(enumArray.Count);
        foreach (var item in enumArray)
        {
            if (item is null)
                continue;

            var value = item.GetValue<string>();
            var label = metadata.OptionLabels is not null && metadata.OptionLabels.TryGetValue(value, out var declared)
                ? declared
                : value;
            options.Add(new ConfigEnumOption(value, label));
        }

        return options;
    }

    private static (ConfigFieldValueKind ValueKind, bool Nullable) ReadValueKind(JsonObject propertySchema, bool hasEnum)
    {
        var types = ReadTypeNames(propertySchema["type"]);
        var nullable = types.Contains("null", StringComparer.Ordinal);
        if (hasEnum || types.Contains("string", StringComparer.Ordinal))
            return (ConfigFieldValueKind.String, nullable);
        if (types.Contains("boolean", StringComparer.Ordinal))
            return (ConfigFieldValueKind.Boolean, nullable);

        throw new InvalidOperationException(
            $"Schema-driven config editor does not yet support field type(s): {string.Join(", ", types)}.");
    }

    private static IReadOnlyList<string> ReadTypeNames(JsonNode? node)
        => node switch
        {
            JsonValue value => [value.GetValue<string>()],
            JsonArray array => [.. array.Where(static item => item is not null).Select(static item => item!.GetValue<string>())],
            _ => []
        };

    private static object? ReadScalar(JsonNode? node)
        => node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<int>(out var number) => number,
            JsonValue value when value.TryGetValue<long>(out var longNumber) => longNumber,
            JsonValue value when value.TryGetValue<double>(out var floatingPoint) => floatingPoint,
            _ => null
        };

    private static string ToDisplayLabel(string propertyName)
    {
        var label = propertyName
            .Replace("Api", "API", StringComparison.Ordinal)
            .Replace("Url", "URL", StringComparison.Ordinal);

        return string.Concat(label.Select((ch, index)
            => index > 0 && char.IsUpper(ch) && !char.IsUpper(label[index - 1]) ? $" {ch}" : ch.ToString()));
    }
}

internal sealed class ConfigSectionEditSession
{
    private readonly NetclawPaths _paths;
    private readonly IReadOnlyList<ProjectedConfigField> _fields;
    private readonly Dictionary<string, ProjectedConfigField> _fieldsByPath;
    private readonly Dictionary<string, object?> _originalValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _currentValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _persistedSecrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _secretPresence = new(StringComparer.Ordinal);
    private readonly bool _secretsFileExists;

    public ConfigSectionEditSession(NetclawPaths paths, IReadOnlyList<ProjectedConfigField> fields)
    {
        _paths = paths;
        _fields = fields;
        _fieldsByPath = fields.ToDictionary(static field => field.Path, StringComparer.Ordinal);
        _secretsFileExists = File.Exists(paths.SecretsPath);

        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        foreach (var field in _fields)
        {
            if (field.Storage == ConfigFieldStorage.SecretsFile)
            {
                var secret = ReadPersistedSecret(secrets, field.Path);
                _persistedSecrets[field.Path] = secret;
                _secretPresence[field.Path] = !string.IsNullOrWhiteSpace(secret);
                _originalValues[field.Path] = null;
                _currentValues[field.Path] = null;
                continue;
            }

            var current = ConfigFileHelper.TryGetPathValue(config, field.Path, out var stored)
                ? NormalizeScalar(field, stored)
                : NormalizeScalar(field, field.DefaultValue);
            _originalValues[field.Path] = current;
            _currentValues[field.Path] = current;
        }
    }

    public IReadOnlyList<ProjectedConfigField> Fields => _fields;

    public bool IsDirty => _fields.Any(IsFieldDirty);

    public object? GetValue(string path)
        => _currentValues.TryGetValue(path, out var value) ? value : null;

    public string GetEditableString(string path)
        => GetValue(path)?.ToString() ?? string.Empty;

    public string? GetEffectiveString(string path)
    {
        var field = GetField(path);
        var current = NormalizeStringValue(GetValue(path));
        if (field.Storage == ConfigFieldStorage.SecretsFile)
            return !string.IsNullOrWhiteSpace(current) ? current : NormalizeStringValue(_persistedSecrets[path]);

        return current;
    }

    public bool IsApplicable(ProjectedConfigField field)
    {
        if (string.IsNullOrWhiteSpace(field.ApplicableWhenPath)
            || string.IsNullOrWhiteSpace(field.ApplicableWhenEquals))
        {
            return true;
        }

        return string.Equals(
            GetValue(field.ApplicableWhenPath)?.ToString(),
            field.ApplicableWhenEquals,
            StringComparison.OrdinalIgnoreCase);
    }

    public bool HasPersistedSecret(string path)
        => _secretPresence.TryGetValue(path, out var present) && present;

    public void SetValue(string path, object? value)
    {
        var field = GetField(path);
        _currentValues[path] = NormalizeScalar(field, value);
    }

    public void ResetDraft()
    {
        foreach (var field in _fields)
        {
            _currentValues[field.Path] = field.Storage == ConfigFieldStorage.SecretsFile
                ? null
                : _originalValues[field.Path];
        }
    }

    public void Save()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;

        foreach (var field in _fields)
        {
            if (!IsFieldDirty(field))
                continue;

            if (field.Storage == ConfigFieldStorage.SecretsFile)
            {
                SaveSecretField(secrets, field);
                continue;
            }

            SaveConfigField(config, field);
        }

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
        if (_secretsFileExists || HasUserSecretData(secrets))
            ConfigFileHelper.WriteSecretsFile(_paths, secrets);

        AcceptCurrentValuesAsOriginal();
    }

    private static bool HasUserSecretData(Dictionary<string, object> secrets)
        => secrets.Keys.Any(static key => !string.Equals(key, "configVersion", StringComparison.Ordinal));

    private void SaveConfigField(Dictionary<string, object> config, ProjectedConfigField field)
    {
        var current = NormalizeScalar(field, _currentValues[field.Path]);
        var shouldRemove = current is null
            || field is { ValueKind: ConfigFieldValueKind.String } && string.IsNullOrWhiteSpace(current.ToString())
            || field.TrimDefaultOnSave && ValuesEqual(current, field.DefaultValue);

        if (shouldRemove)
        {
            ConfigFileHelper.RemovePath(config, field.Path);
            return;
        }

        ConfigFileHelper.SetPathValue(config, field.Path, current);
    }

    private void SaveSecretField(Dictionary<string, object> secrets, ProjectedConfigField field)
    {
        var current = NormalizeStringValue(_currentValues[field.Path]);
        if (string.IsNullOrWhiteSpace(current))
            return;

        ConfigFileHelper.SetPathValue(secrets, field.Path, current);
        _persistedSecrets[field.Path] = current;
        _secretPresence[field.Path] = true;
    }

    private void AcceptCurrentValuesAsOriginal()
    {
        foreach (var field in _fields)
        {
            if (field.Storage == ConfigFieldStorage.SecretsFile)
            {
                _currentValues[field.Path] = null;
                _originalValues[field.Path] = null;
                continue;
            }

            _originalValues[field.Path] = _currentValues[field.Path];
        }
    }

    private bool IsFieldDirty(ProjectedConfigField field)
    {
        if (field.Storage == ConfigFieldStorage.SecretsFile)
            return !string.IsNullOrWhiteSpace(GetEditableString(field.Path));

        return !ValuesEqual(_originalValues[field.Path], _currentValues[field.Path]);
    }

    private ProjectedConfigField GetField(string path)
        => _fieldsByPath.TryGetValue(path, out var field)
            ? field
            : throw new InvalidOperationException($"Unknown projected field '{path}'.");

    private string? ReadPersistedSecret(Dictionary<string, object> secrets, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(secrets, path, out var rawValue)
            || rawValue is null)
        {
            return null;
        }

        return ConfigFileHelper.DecryptIfEncrypted(_paths, rawValue.ToString());
    }

    private static object? NormalizeScalar(ProjectedConfigField field, object? value)
        => field.ValueKind switch
        {
            ConfigFieldValueKind.Boolean => NormalizeBooleanValue(value),
            _ => NormalizeStringValue(value)
        };

    private static object? NormalizeBooleanValue(object? value)
        => value switch
        {
            null => null,
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => value
        };

    private static string? NormalizeStringValue(object? value)
    {
        var text = value?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool ValuesEqual(object? left, object? right)
        => NormalizeComparable(left) == NormalizeComparable(right);

    private static string NormalizeComparable(object? value)
        => value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            _ => value.ToString() ?? string.Empty
        };
}
