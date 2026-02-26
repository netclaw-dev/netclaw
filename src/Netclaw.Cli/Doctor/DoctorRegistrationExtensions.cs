using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Cli.Doctor;

public static class DoctorRegistrationExtensions
{
    public static void AddDoctorChecks(this IServiceCollection services)
    {
        services.AddSingleton<DoctorRunner>();
        services.AddSingleton<IDoctorCheck, ConfigSchemaDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SecretsJsonDoctorCheck>();
    }
}
