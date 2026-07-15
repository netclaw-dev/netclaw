// -----------------------------------------------------------------------
// <copyright file="WebhookRouteStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

public sealed class WebhookRouteStore
{
    private static readonly TimeSpan RouteLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RouteLockPollInterval = TimeSpan.FromMilliseconds(50);

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
    {
        var filePath = GetPath(routeName);
        using var routeLock = AcquireRouteLock(filePath, CancellationToken.None);
        Write(filePath, definition);
    }

    /// <summary>
    /// Reads and conditionally replaces one route while holding a route-scoped interprocess lock.
    /// Returning a null definition leaves the file unchanged.
    /// </summary>
    public TResult Update<TResult>(
        string routeName,
        CancellationToken cancellationToken,
        Func<WebhookRouteConfig?, (WebhookRouteConfig? Definition, TResult Result)> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var filePath = GetPath(routeName);
        using var routeLock = AcquireRouteLock(filePath, cancellationToken);
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

    public bool Delete(string routeName, CancellationToken cancellationToken)
    {
        var filePath = GetPath(routeName);
        using var routeLock = AcquireRouteLock(filePath, cancellationToken);
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

    private static IDisposable AcquireRouteLock(string filePath, CancellationToken cancellationToken)
    {
        var canonicalPath = GetCanonicalLockPath(filePath);
        var lockIdentity = OperatingSystem.IsWindows()
            ? canonicalPath.ToUpperInvariant()
            : canonicalPath;
        var lockId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockIdentity)));
        var lockScope = OperatingSystem.IsWindows() ? @"Global\" : string.Empty;
        var mutex = new Mutex(initiallyOwned: false, $"{lockScope}netclaw-webhook-route-{lockId}");
        var ownsMutex = false;

        try
        {
            try
            {
                ownsMutex = WaitForMutex(mutex, cancellationToken);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
                // The abandoning process exited while holding the route lock. This process now owns it,
                // and the route's atomic file replacement guarantees the existing file is still complete.
                DeleteAbandonedTempFiles(canonicalPath);
            }

            if (!ownsMutex)
                throw new TimeoutException($"Timed out waiting to update webhook route '{Path.GetFileNameWithoutExtension(filePath)}'.");

            return new RouteLock(mutex);
        }
        catch
        {
            if (ownsMutex)
                mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private static bool WaitForMutex(Mutex mutex, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return mutex.WaitOne(RouteLockTimeout);

        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutex.WaitOne(TimeSpan.Zero))
                return true;

            var remaining = RouteLockTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
                return false;

            var wait = remaining < RouteLockPollInterval ? remaining : RouteLockPollInterval;
            if (cancellationToken.WaitHandle.WaitOne(wait))
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string GetCanonicalLockPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Webhook route path has no parent directory.");
        return Path.Combine(GetCanonicalDirectoryPath(directoryPath), Path.GetFileName(fullPath));
    }

    private static string GetCanonicalDirectoryPath(string directoryPath)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("Webhook route path has no root directory.");
        var current = root;
        var relativeDirectory = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relativeDirectory.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, segment));
            var target = directory.ResolveLinkTarget(returnFinalTarget: true);
            current = target is null
                ? directory.FullName
                : GetCanonicalDirectoryPath(target.FullName);
        }

        return current;
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
