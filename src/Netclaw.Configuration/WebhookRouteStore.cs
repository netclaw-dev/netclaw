// -----------------------------------------------------------------------
// <copyright file="WebhookRouteStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
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
            var filePath = GetPath(routeName);
            using var routeLock = AcquireRouteLock(filePath);
            Write(filePath, definition);
        }
    }

    /// <summary>
    /// Reads and conditionally replaces one route while holding a route-scoped interprocess lock.
    /// Returning a null definition leaves the file unchanged.
    /// </summary>
    public TResult Update<TResult>(
        string routeName,
        Func<WebhookRouteConfig?, (WebhookRouteConfig? Definition, TResult Result)> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_sync)
        {
            var filePath = GetPath(routeName);
            using var routeLock = AcquireRouteLock(filePath);
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
    }

    public bool Delete(string routeName)
    {
        lock (_sync)
        {
            var filePath = GetPath(routeName);
            using var routeLock = AcquireRouteLock(filePath);
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

    private static IDisposable AcquireRouteLock(string filePath)
    {
        var canonicalPath = Path.GetFullPath(filePath);
        var lockId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
        var mutex = new Mutex(initiallyOwned: false, $"netclaw-webhook-route-{lockId}");

        try
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The abandoning process exited while holding the route lock. This process now owns it,
                // and the route's atomic file replacement guarantees the existing file is still complete.
                DeleteAbandonedTempFiles(canonicalPath);
            }

            return new RouteLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static void DeleteAbandonedTempFiles(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("Webhook route path has no parent directory.");
        var fileName = Path.GetFileName(filePath);
        foreach (var tempPath in Directory.EnumerateFiles(directory, $"{fileName}.*.tmp"))
            File.Delete(tempPath);
    }

    private sealed class RouteLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
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
