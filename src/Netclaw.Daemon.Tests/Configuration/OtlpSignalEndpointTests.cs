// -----------------------------------------------------------------------
// <copyright file="OtlpSignalEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class OtlpSignalEndpointTests
{
    [Theory]
    [InlineData("http://collector:4318", "v1/logs", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318/", "v1/metrics", "http://collector:4318/v1/metrics")]
    [InlineData("https://gw.example/otel", "v1/metrics", "https://gw.example/otel/v1/metrics")]
    public void HttpProtobuf_AppendsTheSignalPathToTheBaseUrl(
        string configured, string signalPath, string expected)
    {
        var resolved = TelemetryRegistrationExtensions.ResolveSignalEndpoint(
            new Uri(configured), OtlpExportProtocol.HttpProtobuf, signalPath);

        Assert.Equal(expected, resolved.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://collector:4318/v1/logs", "v1/logs", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318/v1/logs", "v1/metrics", "http://collector:4318/v1/metrics")]
    [InlineData("http://collector:4318/v1/logs/", "v1/logs", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318/full/V1/Logs", "v1/metrics", "http://collector:4318/full/v1/metrics")]
    [InlineData("https://gw.example/otel/v1/metrics", "v1/logs", "https://gw.example/otel/v1/logs")]
    [InlineData("https://gw.example/otel/v1/traces", "v1/logs", "https://gw.example/otel/v1/logs")]
    [InlineData("http://collector:4318/mylogs", "v1/logs", "http://collector:4318/mylogs/v1/logs")]
    public void HttpProtobuf_ReducesAFullSignalUrlToItsBaseBeforeAppending(
        string configured, string signalPath, string expected)
    {
        var resolved = TelemetryRegistrationExtensions.ResolveSignalEndpoint(
            new Uri(configured), OtlpExportProtocol.HttpProtobuf, signalPath);

        Assert.Equal(expected, resolved.AbsoluteUri);
    }

    [Theory]
    [InlineData("v1/logs")]
    [InlineData("v1/metrics")]
    public void Grpc_UsesTheEndpointUnchanged(string signalPath)
    {
        var configured = new Uri("http://collector:4317");

        var resolved = TelemetryRegistrationExtensions.ResolveSignalEndpoint(
            configured, OtlpExportProtocol.Grpc, signalPath);

        Assert.Equal(configured, resolved);
    }
}

/// <summary>
/// Runs the real <c>AddNetclawTelemetry</c> registration against a local collector, so the URL each
/// exporter really posts to is observed rather than only the helper that computes it.
/// </summary>
[Collection(OtlpExporterEnvironmentCollection.Name)]
public sealed class OtlpExporterWiringTests
{
    [Fact]
    public async Task HttpProtobuf_PostsLogsAndMetricsToTheirOwnSignalPaths_UnderTheConfiguredBaseUrl()
    {
        var port = FreePort();
        var paths = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var bothSignalsSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var collector = new System.Net.HttpListener();
        collector.Prefixes.Add($"http://127.0.0.1:{port}/");
        collector.Start();
        var serving = Task.Run(async () =>
        {
            while (collector.IsListening)
            {
                System.Net.HttpListenerContext context;
                try
                {
                    context = await collector.GetContextAsync();
                }
                catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                paths.Enqueue(context.Request.Url!.AbsolutePath);
                if (paths.Contains("/collector/v1/logs") && paths.Contains("/collector/v1/metrics"))
                    bothSignalsSeen.TrySetResult();
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }, TestContext.Current.CancellationToken);

        var previousProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        try
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.Configuration["Telemetry:Enabled"] = "true";
            builder.Configuration["Telemetry:Otlp:Endpoint"] = $"http://127.0.0.1:{port}/collector";
            builder.AddNetclawTelemetry();

            await using var app = builder.Build();
            var meterProvider = app.Services.GetRequiredService<MeterProvider>();
            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("otlp-wiring-test").LogInformation("exported");
            Netclaw.Channels.Telemetry.SessionTelemetry.RecordTurnCompleted();
            meterProvider.ForceFlush();
            app.Services.GetRequiredService<LoggerProvider>().ForceFlush();

            // The log batch processor exports on its own schedule (5s by default).
            // A timeout is reported by the path assertions below, which name what arrived instead.
            _ = await Record.ExceptionAsync(() => bothSignalsSeen.Task
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

            Assert.Contains("/collector/v1/logs", paths);
            Assert.Contains("/collector/v1/metrics", paths);
            Assert.DoesNotContain("/collector", paths);
            Assert.DoesNotContain("/collector/v1/metrics/v1/logs", paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", previousProtocol);
            collector.Stop();
            await serving;
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OtlpExporterEnvironmentCollection
{
    public const string Name = "OtlpExporterEnvironment";
}
