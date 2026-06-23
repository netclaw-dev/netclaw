// -----------------------------------------------------------------------
// <copyright file="SchemaDrivenConfigInfrastructure.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
