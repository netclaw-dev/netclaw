// -----------------------------------------------------------------------
// <copyright file="IDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Doctor;

public interface IDoctorCheck
{
    Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default);
}
