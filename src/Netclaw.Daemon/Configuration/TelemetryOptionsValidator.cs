// -----------------------------------------------------------------------
// <copyright file="TelemetryOptionsValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Options;

namespace Netclaw.Daemon.Configuration;

public sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        if (!Uri.TryCreate(options.Otlp.Endpoint, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail(
                "Telemetry:Otlp:Endpoint must be an absolute URI.");
        }

        return ValidateOptionsResult.Success;
    }
}
