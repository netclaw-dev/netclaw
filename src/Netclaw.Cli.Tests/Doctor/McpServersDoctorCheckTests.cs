// -----------------------------------------------------------------------
// <copyright file="McpServersDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class McpServersDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public McpServersDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task NoConfigFile_Passes()
    {
        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task NoMcpServersSection_Passes()
    {
        WriteConfig(new { configVersion = 1 });
        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new { })));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No MCP servers", result.Message);
    }

    [Fact]
    public async Task ValidStdioServer_UnreachableEndpoint_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                memorizer = new
                {
                    Transport = "stdio",
                    Command = "npx",
                    Arguments = new[] { "-y", "@memorizer/mcp-server" },
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(
            _paths,
            CreateDaemonApi(_ => throw new HttpRequestException("daemon offline")),
            (_, _, _) => Task.FromResult(new McpProbeResult(
                McpProbeStatus.Unreachable,
                0,
                "connection failed")));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Single enabled server that can't connect → Error
        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("unreachable", result.Message);
    }

    [Fact]
    public async Task StdioServerMissingCommand_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "stdio",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires 'Command'", result.Message);
    }

    [Fact]
    public async Task HttpServerMissingUrl_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "http",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires 'Url'", result.Message);
    }

    [Fact]
    public async Task InvalidTransport_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "grpc",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("invalid transport", result.Message);
    }

    [Fact]
    public async Task DisabledServer_SkipsProbe()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                disabled_one = new { Transport = "stdio", Command = "npx", Enabled = false }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Only disabled servers → all pass (no enabled servers to fail)
        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("disabled", result.Message);
    }

    [Fact]
    public async Task DaemonReportedAuthFailure_ReturnsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                notion = new
                {
                    Transport = "http",
                    Url = "https://mcp.example.com",
                    Enabled = true,
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            notion = new
            {
                state = "AuthFailed",
                toolCount = 0,
                error = "Authentication rejected by server (401 Unauthorized). Run: netclaw mcp auth notion"
            }
        })));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("auth failed", result.Message);
        Assert.Contains("netclaw mcp auth", result.Remediation);
    }

    [Fact]
    public async Task DaemonReportedAwaitingAuth_ReturnsWarning()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                textforge = new
                {
                    Transport = "http",
                    Url = "https://mcp.example.com",
                    Enabled = true,
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            textforge = new
            {
                state = "AwaitingAuth",
                toolCount = 0,
                error = "OAuth authorization required. Run: netclaw mcp auth textforge"
            }
        })));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("awaiting auth", result.Message);
    }

    [Fact]
    public async Task OfflineOAuthProbe_DoesNotClaimAuthFailure()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                notion = new
                {
                    Transport = "http",
                    Url = "https://mcp.example.com",
                    Enabled = true,
                    OAuthClientId = "client-id"
                }
            }
        });

        var check = new McpServersDoctorCheck(
            _paths,
            CreateDaemonApi(_ => throw new HttpRequestException("daemon offline")),
            (_, _, _) => Task.FromResult(new McpProbeResult(
                McpProbeStatus.AwaitingAuth,
                0,
                null)));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("auth cannot be verified offline", result.Message);
        Assert.DoesNotContain("auth failed", result.Message);
    }

    private void WriteConfig(object config)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-daemon-api-test-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();

        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, paths);
    }

}
