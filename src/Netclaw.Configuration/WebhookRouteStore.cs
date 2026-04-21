using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

public sealed class WebhookRouteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NetclawPaths _paths;
    private readonly object _sync = new();

    public WebhookRouteStore(NetclawPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.WebhooksDirectory);
    }

    /// <summary>
    /// Normalizes a route name to lowercase kebab-case format.
    /// </summary>
    public static string NormalizeRouteName(string value)
        => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Attempts to read a single route by name. More efficient than <see cref="ListRouteFiles"/>
    /// when you only need one route.
    /// </summary>
    public bool TryGet(string routeName, out (string FilePath, WebhookRouteConfig? Definition) result)
    {
        lock (_sync)
        {
            var filePath = GetPath(routeName);
            if (!File.Exists(filePath))
            {
                result = default;
                return false;
            }
            result = (filePath, TryRead(filePath));
            return true;
        }
    }

    public IReadOnlyList<(string RouteName, string FilePath, WebhookRouteConfig? Definition)> ListRouteFiles()
    {
        lock (_sync)
        {
            return Directory.EnumerateFiles(_paths.WebhooksDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(file => (Path.GetFileNameWithoutExtension(file), file, TryRead(file)))
                .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Save(string routeName, WebhookRouteConfig definition)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_paths.WebhooksDirectory);

            var filePath = GetPath(routeName);
            var tempPath = $"{filePath}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
        }
    }

    public bool Delete(string routeName)
    {
        lock (_sync)
        {
            var filePath = GetPath(routeName);
            if (!File.Exists(filePath))
                return false;

            File.Delete(filePath);
            return true;
        }
    }

    private WebhookRouteConfig? TryRead(string filePath)
    {
        try
        {
            return JsonSerializer.Deserialize<WebhookRouteConfig>(File.ReadAllText(filePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string GetPath(string routeName)
        => Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json");
}
