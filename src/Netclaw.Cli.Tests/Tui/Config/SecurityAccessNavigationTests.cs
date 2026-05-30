// -----------------------------------------------------------------------
// <copyright file="SecurityAccessNavigationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SecurityAccessNavigationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SecurityAccessNavigationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task McpGrants_Escape_ReturnsToSecurityUsingTerminaHistory()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" }
            }
            """);

        var app = CreateHeadlessApp(out var input, out var securityVm, out var getMcpVm, out var navigation);
        securityVm.SelectedAudienceIndex.Value = 1;
        securityVm.OpenSelectedAudienceProfile();
        securityVm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.McpPermissions;

        securityVm.ActivateSelectedAudienceProfileRow();
        input.EnqueueKey(ConsoleKey.Escape);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var mcpVm = Assert.IsType<McpToolPermissionsViewModel>(getMcpVm());
        Assert.Equal("/security", app.CurrentPath);
        Assert.Equal(TrustAudience.Team, mcpVm.SelectedAudience);
        Assert.Equal(1, navigation.BackRequestsForTests);
    }

    private TerminaApplication CreateHeadlessApp(
        out VirtualInputSource input,
        out SecurityAccessViewModel securityVm,
        out Func<McpToolPermissionsViewModel?> getMcpVm,
        out TuiNavigation navigation)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        var navigationState = new McpToolPermissionsNavigationState();
        var tuiNavigation = new TuiNavigation();
        SecurityAccessViewModel? capturedSecurityVm = null;
        McpToolPermissionsViewModel? capturedMcpVm = null;

        var configuration = new ConfigurationBuilder().Build();
        var daemonApi = new DaemonApi(new FailingHttpClientFactory(), configuration, _paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddSingleton(navigationState);
        services.AddSingleton(tuiNavigation);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/security", builder =>
        {
            builder.RegisterRoute<SecurityAccessPage, SecurityAccessViewModel>(
                "/security",
                _ => new SecurityAccessPage(),
                _ =>
                {
                    capturedSecurityVm = new SecurityAccessViewModel(_paths, navigationState);
                    return capturedSecurityVm;
                });
            builder.RegisterRoute<McpToolPermissionsPage, McpToolPermissionsViewModel>(
                "/mcp-tools",
                _ => new McpToolPermissionsPage(),
                _ =>
                {
                    capturedMcpVm = new McpToolPermissionsViewModel(_paths, daemonApi, navigationState, tuiNavigation);
                    return capturedMcpVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();
        tuiNavigation.Attach(app);

        securityVm = capturedSecurityVm!;
        getMcpVm = () => capturedMcpVm;
        navigation = tuiNavigation;
        return app;
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("Test: no daemon available");
    }

    private sealed class FailingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailingHttpHandler());
    }
}
