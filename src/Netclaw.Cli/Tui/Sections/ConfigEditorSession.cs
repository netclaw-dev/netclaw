// -----------------------------------------------------------------------
// <copyright file="ConfigEditorSession.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Cli.Secrets;
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
    private readonly List<SectionContribution> _secretContributions = [];

    public ConfigEditorSession(NetclawPaths paths)
    {
        _paths = paths;
        _stateStore = new ConfigEditorStateStore(paths);
        Config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        Secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
    }

    internal Dictionary<string, object> Config { get; }

    internal Dictionary<string, object> Secrets { get; }

    public void Apply(SectionContribution contribution)
    {
        ApplyFieldActions(Config, contribution);
        ApplySecretActions(Secrets, contribution);
        if (HasMutatingSecretActions(contribution))
            _secretContributions.Add(contribution);
        _stateStore.Apply(contribution.StateActionsOrEmpty);
    }

    public void Save()
    {
        _paths.EnsureDirectoriesExist();
        Config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, Config);

        if (_secretContributions.Count > 0)
        {
            ConfigFileHelper.UpdateSecretsFile(_paths, (secrets, fileExisted) =>
            {
                var changed = false;
                foreach (var contribution in _secretContributions)
                    changed |= ApplySecretActions(secrets, contribution);

                return changed && (fileExisted || HasUserSecretData(secrets));
            });
        }
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
                    SetSecretPathValue(secrets, action.Path, action.Value!);
                    changed = true;
                    break;
                case SectionSecretActionKind.Delete:
                    changed |= RemoveSecretPath(secrets, action.Path);
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

    private static bool HasMutatingSecretActions(SectionContribution contribution)
        => contribution.SecretActionsOrEmpty.Any(static action => action.Action is not SectionSecretActionKind.Preserve);

    // Mirrors SecretsJsonUpdater's path-merge (colon-collision cleanup + nested upsert), but over the
    // Dictionary<string, object> shape ConfigFileHelper loads rather than a JsonObject. The two share
    // ParseKeyPath; keep the collision cleanup below in sync with
    // SecretsJsonUpdater.RemoveLiteralCollisionKeys. Note one deliberate difference: this engine
    // rejects a scalar at an intermediate segment (GetOrCreateSection throws) instead of overwriting
    // it the way SecretsJsonUpdater does — see ConfigEditorSessionTests for the pinned behavior.
    private static void SetSecretPathValue(Dictionary<string, object> secrets, string path, object value)
    {
        var segments = SecretsJsonUpdater.ParseKeyPath(path);
        RemoveLiteralCollisionKeys(secrets, segments);

        var current = secrets;
        for (var i = 0; i < segments.Length - 1; i++)
            current = ConfigFileHelper.GetOrCreateSection(current, segments[i]);

        current[segments[^1]] = value;
    }

    private static bool RemoveSecretPath(Dictionary<string, object> secrets, string path)
    {
        var segments = SecretsJsonUpdater.ParseKeyPath(path);
        var changed = RemovePathBySegments(secrets, segments);
        changed |= RemoveLiteralCollisionKeys(secrets, segments);
        return changed;
    }

    private static bool RemovePathBySegments(Dictionary<string, object> root, IReadOnlyList<string> segments)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var next = ConfigFileHelper.GetSectionOrNull(current, segments[i]);
            if (next is null)
                return false;

            current = next;
        }

        var removed = current.Remove(segments[^1]);
        if (removed)
            PruneEmptySections(root, segments);

        return removed;
    }

    private static bool RemoveLiteralCollisionKeys(Dictionary<string, object> root, IReadOnlyList<string> segments)
        => RemoveLiteralCollisionKeys(root, segments, offset: 0);

    private static bool RemoveLiteralCollisionKeys(Dictionary<string, object> current, IReadOnlyList<string> segments, int offset)
    {
        var changed = false;
        for (var end = offset + 2; end <= segments.Count; end++)
            changed |= current.Remove(string.Join(':', segments.Skip(offset).Take(end - offset)));

        if (offset < segments.Count - 1 && ConfigFileHelper.GetSectionOrNull(current, segments[offset]) is { } child)
            changed |= RemoveLiteralCollisionKeys(child, segments, offset + 1);

        return changed;
    }

    private static void PruneEmptySections(Dictionary<string, object> root, IReadOnlyList<string> segments)
    {
        for (var depth = segments.Count - 1; depth > 0; depth--)
        {
            var parent = root;
            for (var i = 0; i < depth - 1; i++)
            {
                var next = ConfigFileHelper.GetSectionOrNull(parent, segments[i]);
                if (next is null)
                    return;

                parent = next;
            }

            var key = segments[depth - 1];
            if (ConfigFileHelper.GetSectionOrNull(parent, key) is { Count: 0 })
                parent.Remove(key);
            else
                return;
        }
    }
}
