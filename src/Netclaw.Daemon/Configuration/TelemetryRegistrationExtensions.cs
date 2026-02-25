using Microsoft.Extensions.Options;
using Netclaw.Channels.Telemetry;
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

        if (!telemetry.Enabled)
            return;

        var endpoint = new Uri(telemetry.Otlp.Endpoint);

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("netclawd"));
            options.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("netclawd"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ChannelTelemetry.MeterName);
                metrics.AddOtlpExporter(otlp => otlp.Endpoint = endpoint);
            });
    }
}
