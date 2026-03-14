using System.Collections.Concurrent;

namespace Netclaw.Configuration;

/// <summary>
/// Thread-safe registry of named <see cref="SubAgentProfile"/> definitions.
/// Supports both code-registered (internal platform agents) and file-loaded
/// (user-facing operator agents) profiles. Singleton — registered in DI.
/// </summary>
public sealed class SubAgentDefinitionRegistry
{
    private readonly ConcurrentDictionary<string, SubAgentProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a subagent profile. Rejects duplicates.
    /// </summary>
    /// <returns><c>true</c> if registered; <c>false</c> if a profile with the same name already exists.</returns>
    public bool Register(SubAgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _profiles.TryAdd(profile.Name, profile);
    }

    /// <summary>
    /// Look up a profile by name (case-insensitive).
    /// </summary>
    public SubAgentProfile? TryGetByName(string name)
    {
        return _profiles.TryGetValue(name, out var profile) ? profile : null;
    }

    /// <summary>
    /// Returns true when a profile with the given name exists.
    /// </summary>
    public bool Contains(string name) => _profiles.ContainsKey(name);

    /// <summary>
    /// Returns all user-facing profiles (visible to <c>spawn_agent</c> and discovery).
    /// </summary>
    public IReadOnlyList<SubAgentProfile> GetUserFacing()
    {
        return _profiles.Values
            .Where(p => p.Visibility == SubAgentVisibility.UserFacing)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns all registered profiles (user-facing and internal).
    /// </summary>
    public IReadOnlyList<SubAgentProfile> GetAll()
    {
        return _profiles.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the count of registered profiles.
    /// </summary>
    public int Count => _profiles.Count;
}
