// -----------------------------------------------------------------------
// <copyright file="TelemetryRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        // Build the OpenTelemetry resource once. NetclawResourceDetector supplies
        // assembly/runtime defaults; AddEnvironmentVariableDetector runs last so the
        // standard OTEL_SERVICE_NAME / OTEL_RESOURCE_ATTRIBUTES env vars override
        // those defaults — netclaw is configured the same way as every other
        // OpenTelemetry service. Projected and registered unconditionally:
        // operational webhook alerts carry the identity even when OTLP export is off.
        var resource = ResourceBuilder.CreateEmpty()
            .AddDetector(new NetclawResourceDetector())
            .AddTelemetrySdk()
            .AddEnvironmentVariableDetector()
            .Build();
        builder.Services.AddSingleton(ProjectServiceIdentity(resource));
        builder.Services.AddHostedService<ServiceIdentityStartupLogger>();

        if (!telemetry.Enabled)
            return;

        var endpoint = new Uri(telemetry.Otlp.Endpoint);

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(resource.Attributes));
            options.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(rb => rb.AddAttributes(resource.Attributes))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ChannelTelemetry.MeterName);
                metrics.AddMeter(SessionTelemetry.MeterName);
                metrics.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
            });
    }

    /// <summary>
    /// Projects the OpenTelemetry <c>service.*</c> resource attributes into a
    /// <see cref="ServiceIdentity"/> for operational webhook payloads, so an alert
    /// carries the same identity as the telemetry this instance emits.
    /// <c>service.namespace</c> and <c>service.instance.id</c> are absent unless
    /// the environment supplies them.
    /// </summary>
    internal static ServiceIdentity ProjectServiceIdentity(Resource resource)
    {
        string? Attribute(string key)
        {
            foreach (var attribute in resource.Attributes)
            {
                if (attribute.Key == key)
                    return attribute.Value as string;
            }

            return null;
        }

        return new ServiceIdentity(
            Attribute("service.name") ?? "netclawd",
            Attribute("service.namespace"),
            Attribute("service.instance.id"),
            Attribute("service.version") ?? "");
    }
}

/// <summary>
/// Logs the resolved <see cref="ServiceIdentity"/> once at daemon startup, so an
/// operator can confirm what identity this instance reports to telemetry and
/// operational webhook alerts (and whether their <c>OTEL_*</c> env vars took effect).
/// </summary>
internal sealed class ServiceIdentityStartupLogger(
    ServiceIdentity identity,
    ILogger<ServiceIdentityStartupLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "OpenTelemetry service identity resolved: service.name={ServiceName}, " +
            "service.namespace={ServiceNamespace}, service.instance.id={ServiceInstanceId}, " +
            "service.version={ServiceVersion}",
            identity.Name,
            identity.Namespace ?? "(unset)",
            identity.InstanceId ?? "(unset)",
            identity.Version);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
