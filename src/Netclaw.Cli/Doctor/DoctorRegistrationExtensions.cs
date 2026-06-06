// -----------------------------------------------------------------------
// <copyright file="DoctorRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Cli.Doctor;

public static class DoctorRegistrationExtensions
{
    public static void AddDoctorChecks(this IServiceCollection services)
    {
        services.AddSingleton<DoctorRunner>();
        services.AddSingleton<DoctorFixService>();
        services.AddSingleton<IDoctorCheck, ConfigSchemaDoctorCheck>();
        services.AddSingleton<IDoctorCheck, ToolAudienceProfilesDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SecurityPolicyDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SlackAclDoctorCheck>();
        services.AddSingleton<IDoctorCheck, TelemetryDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SecretsJsonDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SlackAuthDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SqliteProvisioningDoctorCheck>();
        services.AddSingleton<IDoctorCheck, ImageProcessingDoctorCheck>();
        services.AddSingleton<IDoctorCheck, DaemonCrashDoctorCheck>();
        services.AddSingleton<IDoctorCheck, MemoryCheckpointHealthDoctorCheck>();
        services.AddSingleton<IDoctorCheck, McpServersDoctorCheck>();
        services.AddSingleton<IDoctorCheck, ContextWindowDoctorCheck>();
        services.AddSingleton<IDoctorCheck, UpdateAvailableDoctorCheck>();
        services.AddSingleton<IDoctorCheck, WebhookFormatDoctorCheck>();
        services.AddSingleton<IDoctorCheck, InboundWebhookRoutesDoctorCheck>();
        services.AddSingleton<IDoctorCheck, ExposureModeDoctorCheck>();
        services.AddSingleton<IDoctorCheck, SystemdUnitPathDoctorCheck>();
    }
}
