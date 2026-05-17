// -----------------------------------------------------------------------
// <copyright file="TelemetryRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Options;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Netclaw.Daemon.Configuration;

public static class TelemetryRegistrationExtensions
{
    /// <summary>
    /// OpenTelemetry <c>service.name</c> used when neither
    /// <see cref="TelemetryOptions.ServiceName"/> nor the <c>OTEL_SERVICE_NAME</c>
    /// environment variable is set.
    /// </summary>
    internal const string DefaultServiceName = "netclawd";

    public static void AddNetclawTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<TelemetryOptions>()
            .Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>();

        var telemetry = builder.Configuration
            .GetSection(TelemetryOptions.SectionName)
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        if (!telemetry.Enabled)
            return;

        var endpoint = new Uri(telemetry.Otlp.Endpoint);

        // service.name distinguishes netclaw instances in a shared backend;
        // service.version is the running netclaw build so telemetry is
        // attributable to a specific release.
        var serviceName = ResolveServiceName(
            telemetry,
            Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"));
        var serviceVersion = BuildInfo.Version;

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName, serviceVersion: serviceVersion));
            options.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ChannelTelemetry.MeterName);
                metrics.AddMeter(SessionTelemetry.MeterName);
                metrics.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
            });
    }

    /// <summary>
    /// Resolves the OpenTelemetry <c>service.name</c>. Precedence: an explicit
    /// <see cref="TelemetryOptions.ServiceName"/> wins, then the standard
    /// <c>OTEL_SERVICE_NAME</c> environment variable, then
    /// <see cref="DefaultServiceName"/>.
    /// </summary>
    internal static string ResolveServiceName(TelemetryOptions telemetry, string? otelServiceNameEnv)
    {
        if (!string.IsNullOrWhiteSpace(telemetry.ServiceName))
            return telemetry.ServiceName;

        if (!string.IsNullOrWhiteSpace(otelServiceNameEnv))
            return otelServiceNameEnv;

        return DefaultServiceName;
    }
}
