// -----------------------------------------------------------------------
// <copyright file="FakeWebhookDaemon.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;

namespace Netclaw.Cli.Tests.Webhooks;

/// <summary>
/// A daemon that answers the webhook route resource, and the record of what the
/// CLI asked it. Route mutations are daemon-only, so every write test needs one.
/// </summary>
internal sealed class FakeWebhookDaemon
{
    private readonly List<RecordedCall> _calls = [];

    /// <summary>
    /// Creates the fake. <paramref name="respond"/> answers each request; the
    /// fake records the method, path, and body before it runs.
    /// </summary>
    public FakeWebhookDaemon(NetclawPaths paths, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ClientConfigFile.WriteEndpoint(paths, "http://127.0.0.1:5199");
        Api = new DaemonApi(new FakeHttpClientFactory(request => Record(request, respond)), new ConfigurationBuilder().Build(), paths);
    }

    /// <summary>The client the CLI calls.</summary>
    public DaemonApi Api { get; }

    /// <summary>Every request the CLI made, in order.</summary>
    public IReadOnlyList<RecordedCall> Calls => _calls;

    /// <summary>A daemon that accepts the probe, every upsert, and every delete.</summary>
    public static FakeWebhookDaemon Healthy(NetclawPaths paths)
        => new(paths, request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : RouteList());

    /// <summary>A daemon that is not running: the transport never connects.</summary>
    public static FakeWebhookDaemon Unreachable(NetclawPaths paths)
        => new(paths, _ => throw new HttpRequestException("connection refused"));

    /// <summary>An older daemon: it answers, but has no webhook route resource.</summary>
    public static FakeWebhookDaemon WithoutRouteResource(NetclawPaths paths)
        => new(paths, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

    /// <summary>The empty route list that the availability probe reads.</summary>
    public static HttpResponseMessage RouteList()
        => Json(HttpStatusCode.OK, Array.Empty<object>());

    public static HttpResponseMessage Json<T>(HttpStatusCode status, T body)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    /// <summary>Reads the recorded body of the single upsert the CLI sent.</summary>
    public JsonDocument SingleUpsertBody(string routeName)
    {
        var upsert = _calls.Single(call => call.Method == "PUT");
        if (upsert.Path != $"/api/webhooks/{routeName}")
            throw new InvalidOperationException($"Expected an upsert of '{routeName}', got '{upsert.Path}'.");

        return JsonDocument.Parse(upsert.Body);
    }

    private HttpResponseMessage Record(
        HttpRequestMessage request,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        // ReadAsStream is the synchronous content reader, so the fake handler
        // records the body without a blocking wait on a task.
        var body = string.Empty;
        if (request.Content is { } content)
        {
            using var reader = new StreamReader(content.ReadAsStream(), Encoding.UTF8);
            body = reader.ReadToEnd();
        }

        _calls.Add(new RecordedCall(request.Method.Method, request.RequestUri!.AbsolutePath, body));
        return respond(request);
    }

    internal sealed record RecordedCall(string Method, string Path, string Body);
}
