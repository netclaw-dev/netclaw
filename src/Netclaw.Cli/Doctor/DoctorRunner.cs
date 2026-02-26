namespace Netclaw.Cli.Doctor;

public sealed class DoctorRunner(IEnumerable<IDoctorCheck> checks)
{
    public async Task<DoctorRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DoctorCheckResult>();
        foreach (var check in checks)
            results.Add(await check.RunAsync(cancellationToken));

        var hasErrors = results.Any(r => r.Severity == DoctorSeverity.Error);
        var hasWarnings = results.Any(r => r.Severity == DoctorSeverity.Warning);

        var exitCode = hasErrors ? 1 : hasWarnings ? 2 : 0;
        return new DoctorRunResult(results, exitCode);
    }
}

public sealed record DoctorRunResult(IReadOnlyList<DoctorCheckResult> Results, int ExitCode);
