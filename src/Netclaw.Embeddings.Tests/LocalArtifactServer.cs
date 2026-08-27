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
            // On Windows, Stop()/Close() while a GetContextAsync() is pending makes the
            // pending accept throw HttpListenerException ("I/O operation has been aborted").
            // That exception surfaces asynchronously and can arrive AFTER IsListening has
            // already been flipped to false, so gating on `!IsListening` here is racy and lets
            // the exception escape the loop, faulting _serveLoop and rethrowing from
            // DisposeAsync into whichever test is tearing down. There is no legitimate
            // "retry GetContextAsync" scenario during shutdown, so any HttpListenerException
            // from the accept side terminates the loop gracefully.
            catch (HttpListenerException)
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
            // Windows can surface "The handle is invalid" (ArgumentException) from the pending
            // GetContextAsync when Stop()/Close() runs mid-teardown — same shutdown abort as
            // HttpListenerException/ObjectDisposedException. Treat it as graceful stop too.
            catch (ArgumentException)
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
        // On Windows, DisposeAsync can Stop()/Close() the listener while a request is still
        // being served, which aborts the in-flight response stream (ObjectDisposedException on
        // the response OutputStream, or HttpListenerException). That is a shutdown artifact,
        // not a defect: the fixture is being torn down and the client already got its data.
        // Catch so an in-flight request cannot fault _serveLoop and fail a subsequent test.
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException)
        {
        }
        finally
        {
            // Close() on an already-aborted stream can itself throw; guard the teardown.
            try
            {
                ctx.Response.OutputStream.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
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

        // Network-teardown aborts (HttpListenerException on the pending accept, or
        // ObjectDisposedException on an in-flight response stream) can fault the serve loop
        // on Windows. That is a shutdown artifact, not a defect. Swallow it so a mid-test
        // DisposeAsync can never poison a later test in the same class.
        try
        {
            await _serveLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Network-teardown abort: Windows can surface several exception shapes
            // (HttpListenerException, ObjectDisposedException, ArgumentException "invalid
            // handle") from Stop()/Close() racing the pending accept or an in-flight response.
            // The listener is already stopped and closed, so there is nothing left to release
            // and disposal must never fail a test. SW003 suppressed via .slopwatch/config.json.
        }
    }
}
