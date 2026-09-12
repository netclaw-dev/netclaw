// -----------------------------------------------------------------------
// <copyright file="McpProtocolVersionFallbackTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Netclaw.Configuration.Http;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpProtocolVersionFallbackTests
{
    [Fact]
    public async Task Initialize_fallback_does_not_reuse_discovery_protocol_header()
    {
        var server = new ControlledFallbackHandler();
        using var httpClient = McpHttpClientFactory.Create(server);
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("https://example.invalid/mcp"),
                Name = "controlled-fallback",
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient);

        await using var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("2026-07-28", server.DiscoverProtocolVersion);
        Assert.Null(server.InitializeProtocolVersion);
        Assert.Equal("2025-11-25", server.InitializeBodyVersion);
        Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
    }

    private sealed class ControlledFallbackHandler : HttpMessageHandler
    {
        public string? DiscoverProtocolVersion { get; private set; }

        public string? InitializeProtocolVersion { get; private set; }

        public string? InitializeBodyVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var method = body["method"]!.GetValue<string>();

            return method switch
            {
                "server/discover" => Discover(request, body),
                "initialize" => Initialize(request, body),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                _ => throw new InvalidOperationException($"Unexpected MCP method '{method}'."),
            };
        }

        private HttpResponseMessage Discover(HttpRequestMessage request, JsonObject body)
        {
            DiscoverProtocolVersion = GetProtocolVersion(request);
            return JsonResponse(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body["id"]!.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["supportedVersions"] = new JsonArray("2025-11-25"),
                    ["capabilities"] = new JsonObject(),
                },
            });
        }

        private HttpResponseMessage Initialize(HttpRequestMessage request, JsonObject body)
        {
            InitializeProtocolVersion = GetProtocolVersion(request);
            InitializeBodyVersion = body["params"]!["protocolVersion"]!.GetValue<string>();

            if (InitializeProtocolVersion is not null)
            {
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = body["id"]!.DeepClone(),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32020,
                        ["message"] = "Protocol header and initialize body do not match.",
                    },
                }, HttpStatusCode.BadRequest);
            }

            return JsonResponse(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body["id"]!.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25",
                    ["capabilities"] = new JsonObject(),
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "controlled-fallback",
                        ["version"] = "1.0.0",
                    },
                },
            });
        }

        private static string? GetProtocolVersion(HttpRequestMessage request)
            => request.Headers.TryGetValues(McpHttpClientFactory.ProtocolVersionHeaderName, out var values)
                ? Assert.Single(values)
                : null;

        private static HttpResponseMessage JsonResponse(
            JsonObject body,
            HttpStatusCode statusCode = HttpStatusCode.OK)
            => new(statusCode)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
    }
}
