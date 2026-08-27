// -----------------------------------------------------------------------
// <copyright file="LocalArtifactServer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Sockets;

namespace Netclaw.Embeddings.Tests;

/// <summary>
/// Minimal localhost HTTP server used only by <see cref="EmbeddingModelProvisionerTests"/> so
/// those tests exercise real HTTP download behavior (streaming, byte-exact transfer) without
/// ever reaching the internet or the real HuggingFace allowlist URLs.
/// </summary>
internal sealed class LocalArtifactServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
    private readonly Task _serveLoop;
    private bool _disposed;

    public LocalArtifactServer()
    {
        Port = GetFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _serveLoop = Task.Run(ServeLoopAsync);
    }

    public int Port { get; }

    /// <summary>Registers content to serve at <paramref name="path"/> and returns its full URI.</summary>
    public Uri AddRoute(string path, byte[] content)
    {
        _routes[path] = content;
        return new Uri($"http://127.0.0.1:{Port}{path}");
    }

    private async Task ServeLoopAsync()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException) when (!_listener.IsListening)
            {
                return;
            }

            await HandleAsync(ctx).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (_routes.TryGetValue(ctx.Request.Url!.AbsolutePath, out var bytes))
            {
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        finally
        {
            ctx.Response.OutputStream.Close();
        }
    }

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: some tests dispose the server early (mid-test) to prove a later call
        // makes no network access, then the test class's own DisposeAsync disposes it again.
        if (_disposed)
            return;
        _disposed = true;

        _listener.Stop();
        _listener.Close();
        await _serveLoop.ConfigureAwait(false);
    }
}
