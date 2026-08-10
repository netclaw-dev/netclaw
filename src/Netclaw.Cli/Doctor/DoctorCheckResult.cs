// -----------------------------------------------------------------------
// <copyright file="DoctorCheckResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Doctor;

public sealed record DoctorCheckResult(
    string Name,
    DoctorSeverity Severity,
    string Message,
    string? Remediation = null)
{
    public static DoctorCheckResult Pass(string name, string message) =>
        new(name, DoctorSeverity.Pass, message);

    public static DoctorCheckResult Warning(string name, string message, string? remediation = null) =>
        new(name, DoctorSeverity.Warning, message, remediation);

    public static DoctorCheckResult Error(string name, string message, string? remediation = null) =>
        new(name, DoctorSeverity.Error, message, remediation);
}
