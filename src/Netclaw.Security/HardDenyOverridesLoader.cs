// -----------------------------------------------------------------------
// <copyright file="HardDenyOverridesLoader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Security;

/// <summary>
/// Loads operator-authored hard-deny rules from
/// <c>~/.netclaw/config/hard-deny-overrides.json</c>. The file is optional;
/// missing or empty file returns an empty list.
/// </summary>
/// <remarks>
/// The loader is the trust boundary between operator config and the deny
/// engine. It validates each rule's shape (exactly one match field;
/// refinements only on verb rules; reason required) and rejects malformed
/// input loudly so the operator sees the problem at startup rather than
/// silently dropping rules.
///
/// Rules returned by this loader are concatenated with the daemon's
/// shipped defaults — they cannot disable, weaken, or override the
/// shipped defaults. There is no DSL syntax for "disable" by design.
/// </remarks>
public sealed class HardDenyOverridesLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads the override file and returns its rules. Returns an empty list
    /// when the file does not exist or contains an empty array. Throws
    /// <see cref="InvalidDataException"/> when the file exists but cannot be
    /// parsed or contains a malformed rule — the daemon should fail closed
    /// in that case rather than start with a partially-loaded ruleset.
    /// </summary>
    public IReadOnlyList<HardDenyRule> Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return [];

        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Hard-deny overrides file at '{filePath}' could not be read: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(json))
            return [];

        List<HardDenyRule>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<HardDenyRule>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Hard-deny overrides file at '{filePath}' is malformed JSON: {ex.Message}. The daemon refuses to start with an unloadable override file rather than risk silently dropping rules. Fix or remove the file before restarting.",
                ex);
        }

        if (parsed is null || parsed.Count == 0)
            return [];

        // Validate every rule before returning any. A single malformed rule
        // halts the load — partial loads silently drop rules an operator
        // believed were active, and that is exactly the failure mode the
        // doctor check exists to prevent.
        for (var i = 0; i < parsed.Count; i++)
        {
            var rule = parsed[i];
            if (rule is null)
                throw new InvalidDataException(
                    $"Hard-deny overrides file at '{filePath}' contains a null rule at index {i}.");

            try
            {
                rule.Validate();
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException(
                    $"Hard-deny overrides file at '{filePath}' rule at index {i} is invalid: {ex.Message}",
                    ex);
            }
        }

        return parsed;
    }
}
