// -----------------------------------------------------------------------
// <copyright file="SubAgentDefinitionRegistry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Thread-safe registry of named <see cref="SubAgentProfile"/> definitions.
/// Supports both code-registered (internal platform agents) and file-loaded
/// (user-facing operator agents) profiles. Singleton — registered in DI.
/// </summary>
public sealed class SubAgentDefinitionRegistry
{
    private readonly object _gate = new();
    private Dictionary<string, SubAgentProfile> _registeredProfiles = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SubAgentProfile> _fileProfiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a subagent profile. Rejects duplicates.
    /// </summary>
    /// <returns><c>true</c> if registered; <c>false</c> if a profile with the same name already exists.</returns>
    public bool Register(SubAgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_gate)
        {
            if (_registeredProfiles.ContainsKey(profile.Name) || _fileProfiles.ContainsKey(profile.Name))
                return false;

            _registeredProfiles[profile.Name] = profile;
            return true;
        }
    }

    /// <summary>
    /// Replace all file-loaded profiles with a fresh snapshot from disk.
    /// Profiles that conflict with explicitly registered profiles are skipped.
    /// </summary>
    public IReadOnlyList<string> ReplaceFileProfiles(IEnumerable<SubAgentProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        lock (_gate)
        {
            var next = new Dictionary<string, SubAgentProfile>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<string>();

            foreach (var profile in profiles)
            {
                if (_registeredProfiles.ContainsKey(profile.Name))
                {
                    conflicts.Add(profile.Name);
                    continue;
                }

                next[profile.Name] = profile;
            }

            _fileProfiles = next;
            return conflicts;
        }
    }

    /// <summary>
    /// Look up a profile by name (case-insensitive).
    /// </summary>
    public SubAgentProfile? TryGetByName(string name)
    {
        lock (_gate)
        {
            if (_registeredProfiles.TryGetValue(name, out var registered))
                return registered;

            return _fileProfiles.TryGetValue(name, out var fileLoaded) ? fileLoaded : null;
        }
    }

    /// <summary>
    /// Returns true when a profile with the given name exists.
    /// </summary>
    public bool Contains(string name)
    {
        lock (_gate)
        {
            return _registeredProfiles.ContainsKey(name) || _fileProfiles.ContainsKey(name);
        }
    }

    /// <summary>
    /// Returns all user-facing profiles (visible to <c>spawn_agent</c> and discovery).
    /// </summary>
    public IReadOnlyList<SubAgentProfile> GetUserFacing()
    {
        lock (_gate)
        {
            return _registeredProfiles.Values
                .Concat(_fileProfiles.Values)
                .Where(p => p.Visibility == SubAgentVisibility.UserFacing)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Returns all registered profiles (user-facing and internal).
    /// </summary>
    public IReadOnlyList<SubAgentProfile> GetAll()
    {
        lock (_gate)
        {
            return _registeredProfiles.Values
                .Concat(_fileProfiles.Values)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Returns the count of registered profiles.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _registeredProfiles.Count + _fileProfiles.Count;
            }
        }
    }
}
