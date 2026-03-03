using System.CommandLine;

namespace Netclaw.Daemon;

public class StartupArgs
{
    public string? EnvFile { get; set; }
    
    public IReadOnlyDictionary<string, string>? ExtraEnvVars { get; set; }
    
    public bool MergeConfig { get; set; } = true;
}
