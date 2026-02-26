using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class SecretsJsonDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.SecretsPath))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Secrets JSON",
                $"Secrets file not found at {paths.SecretsPath}.",
                "Create secrets.json if you need provider/slack credentials."));
        }

        try
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
            return Task.FromResult(DoctorCheckResult.Pass("Secrets JSON", "secrets.json parses successfully."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Secrets JSON",
                $"Failed parsing {paths.SecretsPath}: {ex.Message}",
                "Fix malformed JSON in secrets.json."));
        }
    }
}
