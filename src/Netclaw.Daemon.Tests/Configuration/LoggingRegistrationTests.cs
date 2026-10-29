// -----------------------------------------------------------------------
// <copyright file="LoggingRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class LoggingRegistrationTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    /// <summary>
    /// The request-start line holds the full query string, which carries OAuth authorization
    /// codes and the SignalR connection token. Operator log-level settings must not bring it back.
    /// </summary>
    [Theory]
    [InlineData("Logging:LogLevel:Default", "Debug")]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics", "Information")]
    public async Task Request_url_logging_stays_off_when_the_operator_raises_log_levels(string key, string level)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = level });
        builder.ConfigureNetclawLogging(new NetclawPaths(_directory.Path));
        await using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(LoggingRegistrationExtensions.HostingDiagnosticsCategory);

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    public void Dispose() => _directory.Dispose();
}
