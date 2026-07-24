// -----------------------------------------------------------------------
// <copyright file="SecretsFileWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Configuration;

/// <summary>
/// Centralizes all writes to secrets.json, enforcing owner-only file permissions
/// on Unix systems (chmod 600). On Windows, relies on user-profile ACLs.
/// When an <see cref="ISecretsProtector"/> is provided, encrypts all plaintext
/// string leaf values in the JSON tree (values already prefixed with <c>ENC:</c>
/// are skipped — idempotent).
/// </summary>
public static class SecretsFileWriter
{
    private static readonly TimeSpan SecretsLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SecretsLockPollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Write JSON content to the secrets file, creating parent directories as needed.
    /// On Linux/macOS, the file is set to owner-only read/write (chmod 600).
    /// </summary>
    public static void Write(string secretsPath, string json, ISecretsProtector? protector = null)
    {
        using var secretsLock = AcquireSecretsLock(secretsPath, CancellationToken.None);
        WriteUnlocked(secretsPath, json, protector);
    }

    /// <summary>
    /// Read the latest secrets document and replace it while holding a path-scoped interprocess lock.
    /// Returning a null updated root leaves the file unchanged.
    /// </summary>
    public static TResult Update<TResult>(
        string secretsPath,
        Func<JsonObject, bool, (JsonObject? UpdatedRoot, TResult Result)> update,
        JsonSerializerOptions? options = null,
        ISecretsProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        using var secretsLock = AcquireSecretsLock(secretsPath, cancellationToken);
        var fileExists = File.Exists(secretsPath);
        var root = fileExists
            ? ReadUnlocked(secretsPath, protector)
            : [];

        var outcome = update(root, fileExists);
        if (outcome.UpdatedRoot is not null)
            WriteUnlocked(secretsPath, outcome.UpdatedRoot.ToJsonString(options ?? DefaultJsonOptions), protector);

        return outcome.Result;
    }

    private static JsonObject ReadUnlocked(string secretsPath, ISecretsProtector? protector)
    {
        var json = File.ReadAllText(secretsPath);
        if (protector is not null)
            json = DecryptJsonLeaves(json, protector);

        var node = JsonNode.Parse(json);
        return node as JsonObject
               ?? throw new InvalidDataException($"Secrets file '{secretsPath}' must contain a JSON object.");
    }

    private static void WriteUnlocked(string secretsPath, string json, ISecretsProtector? protector)
    {
        if (protector is not null)
            json = EncryptJsonLeaves(json, protector);

        // Atomic rename, with owner-only perms applied to the temp BEFORE it becomes the
        // destination so secrets.json is never momentarily world-readable.
        AtomicFile.WriteAllText(secretsPath, json, hardenTempPermissions: SetOwnerOnlyPermissions);
    }

    /// <summary>
    /// Serialize a dictionary to JSON and write it to the secrets file with hardened permissions.
    /// </summary>
    public static void Write(string secretsPath, Dictionary<string, object> secrets,
        JsonSerializerOptions? options = null, ISecretsProtector? protector = null)
    {
        var json = JsonSerializer.Serialize(secrets, options ?? DefaultJsonOptions);
        Write(secretsPath, json, protector);
    }

    /// <summary>
    /// Walk a JSON tree and encrypt all non-<c>ENC:</c> string leaf values.
    /// Already-encrypted values are left untouched (idempotent).
    /// </summary>
    private static string EncryptJsonLeaves(string json, ISecretsProtector protector)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
            return json;

        EncryptNode(node, protector);
        return node.ToJsonString(DefaultJsonOptions);
    }

    /// <summary>
    /// Walk a JSON tree and decrypt all <c>ENC:</c>-prefixed string leaf values.
    /// Non-encrypted values are left untouched (idempotent).
    /// </summary>
    public static string DecryptJsonLeaves(string json, ISecretsProtector protector)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
            return json;

        DecryptNode(node, protector);
        return node.ToJsonString(DefaultJsonOptions);
    }

    /// <summary>
    /// Count encrypted vs plaintext string leaf values in a JSON document.
    /// </summary>
    public static (int Encrypted, int Plaintext) CountEncryptionStatus(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
            return (0, 0);

        int encrypted = 0, plaintext = 0;
        CountNode(node, ref encrypted, ref plaintext);
        return (encrypted, plaintext);
    }

    private static void EncryptNode(JsonNode node, ISecretsProtector protector)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToArray())
                {
                    if (child is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (!ISecretsProtector.IsEncrypted(s))
                            obj[key] = protector.Protect(s);
                    }
                    else if (child is not null)
                    {
                        EncryptNode(child, protector);
                    }
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    if (item is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (!ISecretsProtector.IsEncrypted(s))
                            arr[i] = protector.Protect(s);
                    }
                    else if (item is not null)
                    {
                        EncryptNode(item, protector);
                    }
                }
                break;
        }
    }

    private static void DecryptNode(JsonNode node, ISecretsProtector protector)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToArray())
                {
                    if (child is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (ISecretsProtector.IsEncrypted(s))
                            obj[key] = protector.Unprotect(s);
                    }
                    else if (child is not null)
                    {
                        DecryptNode(child, protector);
                    }
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    if (item is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (ISecretsProtector.IsEncrypted(s))
                            arr[i] = protector.Unprotect(s);
                    }
                    else if (item is not null)
                    {
                        DecryptNode(item, protector);
                    }
                }
                break;
        }
    }

    private static void CountNode(JsonNode node, ref int encrypted, ref int plaintext)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    if (child is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (ISecretsProtector.IsEncrypted(s))
                            encrypted++;
                        else
                            plaintext++;
                    }
                    else if (child is not null)
                    {
                        CountNode(child, ref encrypted, ref plaintext);
                    }
                }
                break;

            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (ISecretsProtector.IsEncrypted(s))
                            encrypted++;
                        else
                            plaintext++;
                    }
                    else if (item is not null)
                    {
                        CountNode(item, ref encrypted, ref plaintext);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Set owner-only permissions (chmod 600) on Unix. No-op on Windows.
    /// </summary>
    internal static void SetOwnerOnlyPermissions(string path) => AtomicFile.HardenOwnerOnly(path);

    private static IDisposable AcquireSecretsLock(string secretsPath, CancellationToken cancellationToken)
    {
        var canonicalPath = GetCanonicalLockPath(secretsPath);
        var lockIdentity = OperatingSystem.IsWindows()
            ? canonicalPath.ToUpperInvariant()
            : canonicalPath;
        var lockId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockIdentity)));
        var lockScope = OperatingSystem.IsWindows() ? @"Global\" : string.Empty;
        var mutex = new Mutex(initiallyOwned: false, $"{lockScope}netclaw-secrets-file-{lockId}");
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
                // A killed writer may leave only a sibling temp file; the destination remains atomic.
                DeleteAbandonedTempFiles(canonicalPath);
            }

            if (!ownsMutex)
                throw new TimeoutException($"Timed out waiting to update secrets file '{secretsPath}'.");

            return new SecretsLock(mutex);
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
            return mutex.WaitOne(SecretsLockTimeout);

        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutex.WaitOne(TimeSpan.Zero))
                return true;

            var remaining = SecretsLockTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
                return false;

            var wait = remaining < SecretsLockPollInterval ? remaining : SecretsLockPollInterval;
            if (cancellationToken.WaitHandle.WaitOne(wait))
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string GetCanonicalLockPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Secrets path has no parent directory.");
        return Path.Combine(GetCanonicalDirectoryPath(directoryPath), Path.GetFileName(fullPath));
    }

    private static string GetCanonicalDirectoryPath(string directoryPath)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("Secrets path has no root directory.");
        var current = root;
        var relativeDirectory = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relativeDirectory.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var nextPath = Path.Combine(current, segment);
            if (!Directory.Exists(nextPath))
            {
                current = nextPath;
                continue;
            }

            var directory = new DirectoryInfo(nextPath);
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
            ?? throw new InvalidOperationException("Secrets path has no parent directory.");
        var fileName = Path.GetFileName(filePath);
        foreach (var tempPath in Directory.EnumerateFiles(directory, $"{fileName}.tmp-*"))
            File.Delete(tempPath);
    }

    private sealed class SecretsLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
