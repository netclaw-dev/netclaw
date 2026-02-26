namespace Netclaw.Cli.Doctor;

public interface IDoctorCheck
{
    Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default);
}
