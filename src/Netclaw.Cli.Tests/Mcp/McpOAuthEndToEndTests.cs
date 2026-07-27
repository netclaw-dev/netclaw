// -----------------------------------------------------------------------
// <copyright file="McpOAuthEndToEndTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpOAuthEndToEndTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    [Fact]
    public async Task BodylessDcr403TraversesProviderDaemonEndpointSerializationAndCli()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = new NetclawPaths(_directory.Path);
        paths.EnsureDirectoriesExist();
        var configOutput = new StringWriter();
        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "oauth", "https://oauth.test/mcp"],
            paths,
            output: configOutput));
        var servers = McpCommand.LoadMcpServers(paths);
        var logger = new RecordingLogger<McpClientManager>();
        var credentials = new McpOAuthCredentialStore(
            paths,
            TimeProvider.System,
            new NullSecretsProtector(),
            new RecordingLogger<McpOAuthCredentialStore>());
        using var broker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var manager = new McpClientManager(
            servers,
            new ToolRegistry(),
            new ToolConfig(),
            credentials,
            new McpOAuthClientRegistrar(
                new HttpClient(new BodylessDcrHandler()) { BaseAddress = new Uri("https://oauth.test") },
                new RecordingLogger<McpOAuthClientRegistrar>()),
            broker,
            new DaemonConfig(),
            NullNotificationSink.Instance,
            TimeProvider.System,
            new BodylessDcrRuntime(),
            logger,
            new SessionConfig());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(servers);
        builder.Services.AddSingleton(credentials);
        builder.Services.AddSingleton(broker);
        builder.Services.AddSingleton(manager);
        builder.Services.AddSingleton<ILogger<McpClientManager>>(logger);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcpEndpoints();
        await app.StartAsync(ct);

        var daemonApi = new DaemonApi(
            new TestServerHttpClientFactory(app.GetTestClient),
            new ConfigurationBuilder().Build(),
            paths);
        var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "auth", "oauth"],
            paths,
            daemonApi,
            output);

        Assert.Equal(1, exitCode);
        Assert.Contains("dynamic client registration", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("HTTP 403 Forbidden", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Error: \n", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(logger.Messages, message => message.Contains("403", StringComparison.Ordinal));
        Assert.Contains(logger.Exceptions, exception =>
            exception.ToString().Contains("Forbidden", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("access_token", output.ToString(), StringComparison.OrdinalIgnoreCase);
        await manager.StopAsync(CancellationToken.None);
        manager.Dispose();
    }

    public void Dispose() => _directory.Dispose();

    private sealed class BodylessDcrRuntime : IMcpClientRuntime
    {
        public IClientTransport CreateHttpTransport(HttpClientTransportOptions options)
            => new HttpClientTransport(
                options,
                new HttpClient(new BodylessDcrHandler()) { BaseAddress = new Uri("https://oauth.test") },
                ownsHttpClient: true);

        public Task<McpClient> CreateAsync(
            IClientTransport transport,
            McpClientOptions options,
            CancellationToken cancellationToken)
            => McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);

        public async ValueTask<McpClientInitialization> InitializeAsync(
            McpClient client,
            CancellationToken cancellationToken)
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return new McpClientInitialization(tools.Cast<AIFunction>().ToList());
        }

        public ValueTask<object?> InvokeAsync(
            AIFunction function,
            AIFunctionArguments? arguments,
            CancellationToken cancellationToken)
            => function.InvokeAsync(arguments, cancellationToken);

        public ValueTask DisposeAsync(McpClient client) => client.DisposeAsync();
    }

    private sealed class BodylessDcrHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response = path switch
            {
                "/mcp" => Challenge(),
                "/.well-known/oauth-protected-resource/mcp" => Json(new
                {
                    resource = "https://oauth.test/mcp",
                    authorization_servers = new[] { "https://oauth.test" },
                }),
                "/.well-known/oauth-authorization-server" => Json(new
                {
                    issuer = "https://oauth.test",
                    authorization_endpoint = "https://oauth.test/authorize",
                    token_endpoint = "https://oauth.test/token",
                    registration_endpoint = "https://oauth.test/register",
                    response_types_supported = new[] { "code" },
                    grant_types_supported = new[] { "authorization_code", "refresh_token" },
                    token_endpoint_auth_methods_supported = new[] { "none" },
                    code_challenge_methods_supported = new[] { "S256" },
                }),
                "/register" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Challenge()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.TryAddWithoutValidation(
                "WWW-Authenticate",
                "Bearer resource_metadata=\"https://oauth.test/.well-known/oauth-protected-resource/mcp\"");
            return response;
        }

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value),
                    Encoding.UTF8,
                    "application/json"),
            };
    }

    private sealed class TestServerHttpClientFactory(Func<HttpClient> createClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => createClient();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public string? LastMessage { get; private set; }

        public Exception? LastException { get; private set; }

        public List<string> Messages { get; } = [];

        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LastMessage = formatter(state, exception);
            Messages.Add(LastMessage);
            if (exception is not null)
            {
                LastException = exception;
                Exceptions.Add(exception);
            }
        }
    }
}
