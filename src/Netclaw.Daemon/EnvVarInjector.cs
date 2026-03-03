using System;
using System.Collections.Generic;
using System.IO;

namespace Netclaw.Daemon;

public static class EnvVarInjector
{
    public static void ApplyRuntimeCustomization(string? envFile, IReadOnlyDictionary<string, string>? extraVars)
    {
        var merged = new Dictionary<string, string>();

        // Load from file if specified
        if (!string.IsNullOrEmpty(envFile) && File.Exists(envFile))
        {
            foreach (var line in File.ReadAllLines(envFile).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var split = line.Split(=);
                if (split.Length == 2)
                    merged[split[0]] = split[1];
            }
        }

        // Add CLI-provided vars
        if (extraVars != null)
            foreach (var kv in extraVars)
                merged[kv.Key] = kv.Value;

        // Apply to environment for subsequent config loading
        foreach (var kvp in merged)
            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
    }
}
