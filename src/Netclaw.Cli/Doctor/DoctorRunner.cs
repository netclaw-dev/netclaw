// -----------------------------------------------------------------------
// <copyright file="DoctorRunner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Doctor;

public sealed class DoctorRunner(IEnumerable<IDoctorCheck> checks)
{
    /// <summary>
    /// Runs every check. When <paramref name="configLoad"/> is set, the config cannot be loaded:
    /// it leads the report as the one failure, the checks that do not read it still run, and the
    /// rest are counted once as skipped instead of each reporting the same unreadable file.
    /// </summary>
    public async Task<DoctorRunResult> RunAsync(DoctorCheckResult? configLoad = null, CancellationToken cancellationToken = default)
    {
        var results = new List<DoctorCheckResult>();
        var skipped = 0;
        if (configLoad is not null)
            results.Add(configLoad);

        foreach (var check in checks)
        {
            if (configLoad is not null && check is not (ImageProcessingDoctorCheck or SqliteProvisioningDoctorCheck or SystemdUnitPathDoctorCheck or DaemonEnvironmentFileModeDoctorCheck))
            {
                skipped++;
                continue;
            }

            try
            {
                results.Add(await check.RunAsync(cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(DoctorCheckResult.Error(check.GetType().Name, ex.Message.ReplaceLineEndings(" ")));
            }
        }

        if (skipped > 0)
            results.Add(DoctorCheckResult.Warning("Skipped checks", $"{skipped} check(s) skipped: the config could not be read."));

        var hasErrors = results.Any(r => r.Severity == DoctorSeverity.Error);
        var hasWarnings = results.Any(r => r.Severity == DoctorSeverity.Warning);

        var exitCode = hasErrors ? 1 : hasWarnings ? 2 : 0;
        return new DoctorRunResult(results, exitCode);
    }
}

public sealed record DoctorRunResult(IReadOnlyList<DoctorCheckResult> Results, int ExitCode)
{
    /// <summary>A fix that could not be written is a failed run (exit 1), also when the checks found only warnings (exit 2).</summary>
    public int ExitCodeAfterFix(bool fixFailed) => fixFailed ? 1 : ExitCode;
}
