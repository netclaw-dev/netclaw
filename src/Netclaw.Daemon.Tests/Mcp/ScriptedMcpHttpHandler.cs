// -----------------------------------------------------------------------
// <copyright file="ScriptedMcpHttpHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// In-process streamable-HTTP MCP server. It completes the handshake and passes every
/// other POST body to the <c>respond</c> callback.
/// </summary>
internal sealed class ScriptedMcpHttpHandler(Func<JsonObject, HttpResponseMessage> respond) : HttpMessageHandler
{
    private const string ProtocolVersion = "2025-11-25";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // No standalone GET stream and no session DELETE.
        if (request.Method != HttpMethod.Post || request.Content is null)
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

        var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        return body["method"]?.GetValue<string>() switch
        {
            "server/discover" => Json(Result(body, new JsonObject
            {
                ["supportedVersions"] = new JsonArray(ProtocolVersion),
                ["capabilities"] = new JsonObject(),
            })),
            "initialize" => Json(Result(body, new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["serverInfo"] = new JsonObject { ["name"] = "scripted", ["version"] = "1.0.0" },
            })),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            _ => respond(body),
        };
    }

    public static JsonObject Result(JsonObject request, JsonObject result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = result,
        };

    public static HttpResponseMessage Json(JsonObject body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage EventStream(params JsonObject[] messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
            builder.Append("event: message\ndata: ").Append(message.ToJsonString()).Append("\n\n");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    /// <summary>The #2258 response: a reverse proxy 502 with an HTML body.</summary>
    public static HttpResponseMessage BadGateway()
        => new(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(
                "<html>\r\n<head><title>502 Bad Gateway</title></head>\r\n<body>\r\n<center>openresty</center>\r\n</body>\r\n</html>",
                Encoding.UTF8,
                "text/html"),
        };
}
