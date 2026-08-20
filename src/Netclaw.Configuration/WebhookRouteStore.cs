// -----------------------------------------------------------------------
// <copyright file="WebhookRouteStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes the per-route webhook JSON files. One route is one file.
/// <para>
/// The store takes no lock. Inside the daemon, <c>WebhookRouteActor</c> is the
/// only writer, and its mailbox serializes every read-modify-write. Each write
/// is still atomic on its own: the store writes a temporary file and then
/// replaces the route file in one move, so no reader ever sees a partial file.
/// </para>
/// </summary>
public sealed class WebhookRouteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NetclawPaths _paths;

    public WebhookRouteStore(NetclawPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.WebhooksDirectory);
    }

    /// <summary>
    /// Attempts to read a single route by name. More efficient than <see cref="ListRouteFiles"/>
    /// when you only need one route.
    /// </summary>
    public bool TryGet(string routeName, out (string FilePath, WebhookRouteConfig? Definition) result)
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

    public IReadOnlyList<(string RouteName, string FilePath, WebhookRouteConfig? Definition)> ListRouteFiles()
        => Directory.EnumerateFiles(_paths.WebhooksDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => (Path.GetFileNameWithoutExtension(file), file, TryRead(file)))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Save(string routeName, WebhookRouteConfig definition)
        => Write(GetPath(routeName), definition);

    /// <summary>
    /// Reads one route, gives it to <paramref name="update"/>, and writes the
    /// result back. Returning a null definition leaves the file unchanged.
    /// The caller owns the serialization of concurrent updates.
    /// </summary>
    public TResult Update<TResult>(
        string routeName,
        Func<WebhookRouteConfig?, (WebhookRouteConfig? Definition, TResult Result)> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var filePath = GetPath(routeName);
        WebhookRouteConfig? existing = null;
        if (File.Exists(filePath))
        {
            existing = TryRead(filePath);
            if (existing is null)
                throw new InvalidDataException($"Existing webhook route '{routeName}' could not be parsed.");
        }

        var outcome = update(existing);
        if (outcome.Definition is not null)
            Write(filePath, outcome.Definition);

        return outcome.Result;
    }

    public bool Delete(string routeName)
    {
        var filePath = GetPath(routeName);
        if (!File.Exists(filePath))
            return false;

        File.Delete(filePath);
        return true;
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

    private void Write(string filePath, WebhookRouteConfig definition)
    {
        Directory.CreateDirectory(_paths.WebhooksDirectory);
        var tempPath = $"{filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private string GetPath(string routeName)
    {
        var normalizedRouteName = WebhookRouteName.Create(routeName).Value;
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
