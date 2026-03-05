using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// File-backed reminder definition store.
///
/// Reminder schedules are durable in Akka.Reminders (SQLite), but the execution
/// contract and mutable task content are sourced from these JSON files.
/// Scheduled messages only carry a pointer (reminder ID).
/// </summary>
public sealed class ReminderDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly object _sync = new();

    public ReminderDefinitionStore(NetclawPaths paths)
    {
        _directory = paths.RemindersDirectory;
        Directory.CreateDirectory(_directory);
    }

    public bool Exists(ReminderId id)
    {
        lock (_sync)
            return File.Exists(GetPath(id));
    }

    public ReminderDefinition? Get(ReminderId id)
    {
        lock (_sync)
        {
            var path = GetPath(id);
            if (!File.Exists(path))
                return null;

            return ReadDefinition(path);
        }
    }

    public IReadOnlyList<ReminderDefinition> List()
    {
        lock (_sync)
        {
            var list = new List<ReminderDefinition>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var definition = ReadDefinition(file);
                if (definition is not null)
                    list.Add(definition);
            }

            return list;
        }
    }

    public void Save(ReminderDefinition definition)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);

            var path = GetPath(new ReminderId(definition.Id));
            var tempPath = $"{path}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    public bool Delete(ReminderId id)
    {
        lock (_sync)
        {
            var path = GetPath(id);
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
    }

    private string GetPath(ReminderId id)
    {
        var encoded = Uri.EscapeDataString(id.Value);
        return Path.Combine(_directory, $"{encoded}.json");
    }

    private static ReminderDefinition? ReadDefinition(string path)
    {
        var text = File.ReadAllText(path);
        var definition = JsonSerializer.Deserialize<ReminderDefinition>(text, JsonOptions);
        if (definition is null || string.IsNullOrWhiteSpace(definition.Id))
            return null;

        return definition;
    }
}
