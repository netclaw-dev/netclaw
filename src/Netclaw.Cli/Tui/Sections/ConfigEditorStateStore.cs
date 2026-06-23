// -----------------------------------------------------------------------
// <copyright file="ConfigEditorStateStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Passive editor-only state for values that must be dormant while inactive.
/// The daemon never reads this file; runtime config stays in <c>netclaw.json</c>.
/// </summary>
internal sealed class ConfigEditorStateStore(NetclawPaths paths)
{
    private const string FileName = "editor-state.json";
    private const string SectionsKey = "Sections";

    private string StatePath => Path.Combine(paths.ConfigDirectory, FileName);

    internal void Apply(IEnumerable<SectionEditorStateAction> actions)
    {
        var actionList = actions.ToArray();
        if (actionList.Length == 0)
            return;

        var state = LoadState();
        var sections = ConfigFileHelper.GetOrCreateSection(state, SectionsKey);

        foreach (var action in actionList)
        {
            var section = ConfigFileHelper.GetOrCreateSection(sections, action.SectionId);
            switch (action.Action)
            {
                case SectionEditorStateActionKind.Set:
                    section[action.Key] = action.Value!;
                    break;
                case SectionEditorStateActionKind.Delete:
                    section.Remove(action.Key);
                    break;
            }
        }

        WriteState(state);
    }

    internal bool TryGetValue(string sectionId, string key, out object? value)
    {
        var state = LoadState();
        value = null;

        if (ConfigFileHelper.GetSectionOrNull(state, SectionsKey) is not { } sections
            || ConfigFileHelper.GetSectionOrNull(sections, sectionId) is not { } section
            || !section.TryGetValue(key, out var rawValue))
        {
            return false;
        }

        value = NormalizeValue(rawValue);
        return true;
    }

    private Dictionary<string, object> LoadState()
    {
        if (!File.Exists(StatePath))
            return new Dictionary<string, object> { ["configVersion"] = 1 };

        return ConfigFileHelper.LoadJsonDict(StatePath);
    }

    private void WriteState(Dictionary<string, object> state)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        ConfigFileHelper.WriteConfigFile(StatePath, state);
    }

    private static object? NormalizeValue(object? value)
        => value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.Array
                => JsonSerializer.Deserialize<object[]>(element.GetRawText()),
            JsonElement element when element.ValueKind == JsonValueKind.String
                => element.GetString(),
            JsonElement element when element.ValueKind == JsonValueKind.True
                => true,
            JsonElement element when element.ValueKind == JsonValueKind.False
                => false,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue)
                => longValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number
                => element.GetDouble(),
            _ => value
        };
}
