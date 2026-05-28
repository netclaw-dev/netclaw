// -----------------------------------------------------------------------
// <copyright file="ConfigEditorSession.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Shared merge pipeline for config leaf editors. It applies explicit editor
/// contributions to runtime config, secrets, and passive editor state.
/// </summary>
internal sealed class ConfigEditorSession
{
    private readonly NetclawPaths _paths;
    private readonly ConfigEditorStateStore _stateStore;
    private readonly bool _secretsFileExists;
    private bool _secretsChanged;

    public ConfigEditorSession(NetclawPaths paths)
    {
        _paths = paths;
        _stateStore = new ConfigEditorStateStore(paths);
        _secretsFileExists = File.Exists(paths.SecretsPath);
        Config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        Secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
    }

    internal Dictionary<string, object> Config { get; }

    internal Dictionary<string, object> Secrets { get; }

    public void Apply(SectionContribution contribution)
    {
        ApplyFieldActions(Config, contribution);
        _secretsChanged |= ApplySecretActions(Secrets, contribution);
        _stateStore.Apply(contribution.StateActionsOrEmpty);
    }

    public void Save()
    {
        _paths.EnsureDirectoriesExist();
        Config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, Config);

        if (_secretsChanged && (_secretsFileExists || HasUserSecretData(Secrets)))
            ConfigFileHelper.WriteSecretsFile(_paths, Secrets);
    }

    internal static bool ApplyFieldActions(Dictionary<string, object> config, SectionContribution contribution)
    {
        var changed = false;
        foreach (var action in contribution.FieldActionsOrEmpty)
        {
            switch (action.Action)
            {
                case SectionFieldActionKind.Set:
                    ConfigFileHelper.SetPathValue(config, action.Path, action.Value);
                    changed = true;
                    break;
                case SectionFieldActionKind.Delete:
                    changed |= ConfigFileHelper.RemovePath(config, action.Path);
                    break;
            }
        }

        return changed;
    }

    internal static bool ApplySecretActions(Dictionary<string, object> secrets, SectionContribution contribution)
    {
        var changed = false;
        foreach (var action in contribution.SecretActionsOrEmpty)
        {
            switch (action.Action)
            {
                case SectionSecretActionKind.Preserve:
                    break;
                case SectionSecretActionKind.Set:
                    ConfigFileHelper.SetPathValue(secrets, action.Path, action.Value);
                    changed = true;
                    break;
                case SectionSecretActionKind.Delete:
                    changed |= ConfigFileHelper.RemovePath(secrets, action.Path);
                    break;
            }
        }

        return changed;
    }

    internal static void ApplyEditorStateActions(
        NetclawPaths paths,
        IEnumerable<SectionContribution> contributions)
    {
        var stateStore = new ConfigEditorStateStore(paths);
        foreach (var contribution in contributions)
            stateStore.Apply(contribution.StateActionsOrEmpty);
    }

    private static bool HasUserSecretData(Dictionary<string, object> secrets)
        => secrets.Keys.Any(static key => !string.Equals(key, "configVersion", StringComparison.Ordinal));
}
