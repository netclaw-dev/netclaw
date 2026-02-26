using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class DoctorFixService(NetclawPaths paths)
{
    public Task<DoctorFixPlan> BuildPlanAsync(CancellationToken cancellationToken = default)
    {
        var fixes = new List<DoctorFileFix>();

        if (!File.Exists(paths.NetclawConfigPath))
            return Task.FromResult(new DoctorFixPlan(fixes));

        string original;
        JsonObject? obj;
        try
        {
            original = File.ReadAllText(paths.NetclawConfigPath);
            obj = JsonNode.Parse(original) as JsonObject;
        }
        catch
        {
            return Task.FromResult(new DoctorFixPlan(fixes));
        }

        if (obj is null)
            return Task.FromResult(new DoctorFixPlan(fixes));

        if (obj["configVersion"] is null)
        {
            obj["configVersion"] = 1;

            var normalized = obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var replacement = normalized.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                ? normalized
                : normalized + Environment.NewLine;

            fixes.Add(new DoctorFileFix(
                FilePath: paths.NetclawConfigPath,
                Description: "Add missing configVersion with schema version 1.",
                OriginalText: original,
                UpdatedText: replacement));
        }

        return Task.FromResult(new DoctorFixPlan(fixes));
    }

    public async Task ApplyAsync(DoctorFixPlan plan, CancellationToken cancellationToken = default)
    {
        foreach (var fix in plan.Fixes)
            await File.WriteAllTextAsync(fix.FilePath, fix.UpdatedText, cancellationToken);
    }
}

public sealed record DoctorFixPlan(IReadOnlyList<DoctorFileFix> Fixes)
{
    public bool HasChanges => Fixes.Count > 0;
}

public sealed record DoctorFileFix(
    string FilePath,
    string Description,
    string OriginalText,
    string UpdatedText);
