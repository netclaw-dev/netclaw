// -----------------------------------------------------------------------
// <copyright file="SecretsFileWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
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

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Write JSON content to the secrets file, creating parent directories as needed.
    /// On Linux/macOS, the file is set to owner-only read/write (chmod 600).
    /// </summary>
    public static void Write(string secretsPath, string json, ISecretsProtector protector)
    {
        using var secretsLock = AcquireSecretsLock(secretsPath, CancellationToken.None);
        WriteUnlocked(secretsPath, json, protector);
    }

    /// <summary>
    /// Read the latest secrets document and replace it while holding a path-scoped lock.
    /// Returning a null updated root leaves the file unchanged.
    /// </summary>
    public static TResult Update<TResult>(
        string secretsPath,
        Func<JsonObject, bool, (JsonObject? UpdatedRoot, TResult Result)> update,
        ISecretsProtector protector,
        JsonSerializerOptions? options = null,
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

    private static JsonObject ReadUnlocked(string secretsPath, ISecretsProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var json = DecryptJsonLeaves(File.ReadAllText(secretsPath), protector);

        var node = JsonNode.Parse(json);
        return node as JsonObject
               ?? throw new InvalidDataException($"Secrets file '{secretsPath}' must contain a JSON object.");
    }

    private static void WriteUnlocked(string secretsPath, string json, ISecretsProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        json = EncryptJsonLeaves(json, protector);

        // Atomic rename, with owner-only perms applied to the temp BEFORE it becomes the
        // destination so secrets.json is never momentarily world-readable.
        AtomicFile.WriteAllText(secretsPath, json, hardenTempPermissions: SetOwnerOnlyPermissions);
    }

    /// <summary>
    /// Serialize a dictionary to JSON and write it to the secrets file with hardened permissions.
    /// </summary>
    public static void Write(string secretsPath, Dictionary<string, object> secrets,
        ISecretsProtector protector, JsonSerializerOptions? options = null)
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

    /// <summary>
    /// Serializes read-modify-write against one secrets file <em>within this process</em>.
    ///
    /// This does NOT serialize against another process. Nothing stops `netclaw secrets set`
    /// or `netclaw provider add` from running while the daemon is live, and those paths still
    /// write the file without taking this gate, so a CLI write can still lose against a
    /// concurrent daemon token refresh. That hazard predates this type — every caller was an
    /// unlocked read-modify-write before it existed — and closing it needs both a
    /// cross-process lock and the remaining callers moved onto <see cref="Update"/>. Both are
    /// deferred; do not read this gate as covering them.
    /// </summary>
    private static IDisposable AcquireSecretsLock(string secretsPath, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(GateKey(secretsPath), static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(SecretsLockTimeout, cancellationToken))
            throw new TimeoutException($"Timed out waiting to update secrets file '{secretsPath}'.");
        return new SecretsLock(gate);
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Two spellings of the same file must share one gate, so a symlinked config directory
    /// does not hand concurrent writers separate locks and lose an update. Only the
    /// immediate parent is resolved; a symlink deeper in the path is not a shape
    /// <see cref="NetclawPaths"/> produces.
    /// </summary>
    private static string GateKey(string secretsPath)
    {
        var fullPath = Path.GetFullPath(secretsPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Secrets path has no parent directory.");
        if (Directory.Exists(directory))
        {
            directory = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? directory;
        }

        var key = Path.Combine(directory, Path.GetFileName(fullPath));
        return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
    }

    private sealed class SecretsLock(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
