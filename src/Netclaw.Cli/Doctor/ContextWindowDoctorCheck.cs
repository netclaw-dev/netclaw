using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class ContextWindowDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (error is not null)
            return Task.FromResult(error);

        if (root is null)
            return Task.FromResult(DoctorCheckResult.Pass("Context Window", "No config file to check."));

        var models = root["Models"] as JsonObject;
        var main = models?["Main"] as JsonObject;

        if (main is null)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Context Window",
                "No Models.Main section in config. Using default context window (32,768 tokens).",
                "Add a Models.Main section with ContextWindow to netclaw.json."));
        }

        var contextWindow = main["ContextWindow"];
        if (contextWindow is null)
        {
            var modelId = main["ModelId"]?.GetValue<string>() ?? "unknown";
            return Task.FromResult(DoctorCheckResult.Warning(
                "Context Window",
                $"No explicit context window configured for {modelId}. Using default 32,768 tokens.",
                "Set Models.Main.ContextWindow in netclaw.json to clamp the effective runtime context window if needed."));
        }

        if (contextWindow.GetValue<int>() is var cw and > 0)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                "Context Window",
                $"Context window explicitly set to {cw:N0} tokens."));
        }

        return Task.FromResult(DoctorCheckResult.Error(
            "Context Window",
            "Models.Main.ContextWindow must be a positive integer.",
            "Set Models.Main.ContextWindow to the effective runtime context window size in tokens."));
    }
}
