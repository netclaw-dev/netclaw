// -----------------------------------------------------------------------
// <copyright file="SecretsFileWriter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
}
