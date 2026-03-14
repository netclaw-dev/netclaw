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
                "Add a Models.Main section with ContextWindowOverride to netclaw.json."));
        }

        var contextWindow = main["ContextWindowOverride"];
        if (contextWindow is null)
        {
            var modelId = main["ModelId"]?.GetValue<string>() ?? "unknown";
            return Task.FromResult(DoctorCheckResult.Warning(
                "Context Window",
                $"No explicit context window configured for {modelId}. Using default 32,768 tokens.",
                "Set Models.Main.ContextWindowOverride in netclaw.json to match your model's effective context window."));
        }

        if (contextWindow.GetValue<int>() is var cw and > 0)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                "Context Window",
                $"Context window explicitly set to {cw:N0} tokens."));
        }

        return Task.FromResult(DoctorCheckResult.Error(
            "Context Window",
            "Models.Main.ContextWindowOverride must be a positive integer.",
            "Set Models.Main.ContextWindowOverride to the model's effective context window size in tokens."));
    }
}
