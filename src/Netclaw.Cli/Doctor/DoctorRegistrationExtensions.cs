using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Cli.Doctor;

public static class DoctorRegistrationExtensions
{
    public static void AddDoctorChecks(this IServiceCollection services)
    {
        services.AddSingleton<DoctorRunner>();
        services.AddSingleton<DoctorFixService>();
        services.AddSingleton<IDoctorCheck, ConfigSchemaDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SlackAclDoctorCheck>();
        services.AddSingleton<IDoctorCheck, TelemetryDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SecretsJsonDoctorCheck>();
    }
}
