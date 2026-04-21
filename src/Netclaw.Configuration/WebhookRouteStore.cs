using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

public sealed class WebhookRouteStore
{
    private static readonly Regex RouteNamePattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
    {
        if (!TryNormalizeRouteName(value, out var normalized, out var error))
            throw new ArgumentException(error, nameof(value));

        return normalized;
    }

    /// <summary>
    /// Attempts to normalize and validate a route name.
    /// </summary>
    public static bool TryNormalizeRouteName(string value, out string normalized, out string? error)
    {
        normalized = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Webhook route name is required.";
            return false;
        }

        if (!RouteNamePattern.IsMatch(normalized))
        {
            error =
                "Webhook route name must be lowercase kebab-case (letters, numbers, single dashes).";
            return false;
        }

        error = null;
        return true;
    }

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
    {
        var normalizedRouteName = NormalizeRouteName(routeName);
        var webhooksRootPath = Path.GetFullPath(_paths.WebhooksDirectory);
        var path = Path.GetFullPath(Path.Combine(webhooksRootPath, $"{normalizedRouteName}.json"));

        var rootPrefix = webhooksRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? webhooksRootPath
            : webhooksRootPath + Path.DirectorySeparatorChar;

        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Webhook route path resolved outside the webhooks directory.");

        return path;
    }
}
