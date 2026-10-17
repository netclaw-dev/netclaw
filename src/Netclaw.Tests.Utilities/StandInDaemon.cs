// -----------------------------------------------------------------------
// <copyright file="StandInDaemon.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// One request that the listener of a <see cref="StandInDaemon"/> received.
/// </summary>
internal sealed record RecordedRequest(string Method, string Path, string Query)
{
    public const string ShutdownPath = "/api/lifecycle/shutdown";

    /// <summary>The <c>reason</c> query value, or <c>null</c> when the request has none.</summary>
    public string? Reason => HttpUtility.ParseQueryString(Query)["reason"];
}

/// <summary>
/// The only way a test starts a stand-in for <c>netclawd</c>. A stand-in has three parts:
/// a process that <c>netclaw daemon stop</c> accepts as a daemon, a daemon port in the
/// config of the test home, and a recording HTTP listener that the test owns on that port.
/// </summary>
/// <remarks>
/// <para>
/// <c>netclaw daemon stop</c> and the factory reset send a shutdown notice to the endpoint
/// of the home they act on. A home with no port resolves to <c>http://127.0.0.1:5199</c>.
/// On a developer machine, port 5199 belongs to the real daemon. Before this helper, each
/// full run of <c>Netclaw.Cli.Tests</c> sent 10 notices to the real daemon. With this
/// helper, the notice reaches the listener of the test, and the test can assert on it.
/// </para>
/// <para>
/// A source scan (<c>StandInDaemonGuardTests</c>) fails the build when a test starts a
/// stand-in in another way.
/// </para>
/// </remarks>
internal sealed class StandInDaemon : IDisposable
{
    private const int BindAttempts = 5;
    private const int RealDaemonPort = 5199;

    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly HttpListener _listener;
    private readonly Task _acceptLoop;
    private readonly string? _standInFolder;
    private bool _disposed;

    private StandInDaemon(string home, int port, HttpListener listener, Process? process, string? standInFolder)
    {
        Home = home;
        Port = port;
        _listener = listener;
        Process = process;
        _standInFolder = standInFolder;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The Netclaw home that this stand-in serves.</summary>
    public string Home { get; }

    /// <summary>The daemon port in the config of <see cref="Home"/>.</summary>
    public int Port { get; }

    /// <summary>The stand-in process, or <c>null</c> for a listener-only home.</summary>
    public Process? Process { get; }

    /// <summary>The PID that <c>netclaw.pid</c> of <see cref="Home"/> holds.</summary>
    public int Pid => Process?.Id
        ?? throw new InvalidOperationException("This stand-in has no process. It is a listener-only home.");

    public bool HasExited => Process?.HasExited ?? false;

    /// <summary>Waits for the stand-in process to exit. A listener-only home has no process and returns <c>false</c>.</summary>
    public bool WaitForExit(int milliseconds) => Process?.WaitForExit(milliseconds) ?? false;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
                return [.. _requests];
        }
    }

    /// <summary>The shutdown notices that reached the listener, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> ShutdownNotices
        => [.. Requests.Where(static r => r.Path == RecordedRequest.ShutdownPath)];

    /// <summary>
    /// Starts a stand-in daemon for <paramref name="home"/>: the process, its PID file, the
    /// port in the home config, and the recording listener.
    /// </summary>
    /// <param name="home">The Netclaw home. The test owns and deletes it.</param>
    /// <param name="scratchDirectory">A folder that the test owns. It holds the stand-in executable.</param>
    public static StandInDaemon Start(string home, string scratchDirectory)
    {
        var (listener, port) = BindListener();
        string? folder = null;
        Process? process = null;
        try
        {
            WriteDaemonPort(home, port);

            folder = Path.Combine(scratchDirectory, $"stand-in-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var executable = Path.Combine(folder, "netclawd");
            SleepStandIn.Create(executable);

            var psi = new ProcessStartInfo(executable, "600") { UseShellExecute = false };
            // The home this daemon serves, as the environment of a real daemon says.
            psi.Environment["NETCLAW_HOME"] = home;
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("The stand-in daemon did not start.");

            // No lock file on purpose: a lock held by the test process would be released by an
            // Exited callback that can run after the CLI has already seen the process exit.
            File.WriteAllText(
                Path.Combine(home, "netclaw.pid"),
                process.Id.ToString(CultureInfo.InvariantCulture));

            return new StandInDaemon(home, port, listener, process, folder);
        }
        catch
        {
            if (process is { HasExited: false })
                process.Kill();
            process?.Dispose();
            listener.Close();
            if (folder is not null)
                DisposableTempDir.Delete(folder);
            throw;
        }
    }

    /// <summary>
    /// Gives <paramref name="home"/> its own port and a recording listener, with no process and
    /// no PID file. Use it for a home that a command acts on while another home holds the
    /// process, so that no notice from that command can reach port 5199.
    /// </summary>
    public static StandInDaemon ListenOnly(string home)
    {
        var (listener, port) = BindListener();
        try
        {
            WriteDaemonPort(home, port);
            return new StandInDaemon(home, port, listener, process: null, standInFolder: null);
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    /// <summary>
    /// Throws when a command can send a shutdown notice for <paramref name="home"/> and neither the
    /// home nor the child environment gives a daemon port. A command reaches the notice when the
    /// home has a PID file. Without a port, the notice would go to port 5199, the port of the real
    /// daemon.
    /// </summary>
    /// <remarks>
    /// A port is the proof, not <c>NETCLAW_DAEMON_ENDPOINT</c>. The local-control paths
    /// (<c>daemon status</c>, <c>daemon stop</c>, <c>pair</c>) ignore that variable and read only the
    /// <c>Daemon</c> config, so an endpoint variable alone leaves them on port 5199.
    /// </remarks>
    /// <param name="home">The Netclaw home that the child process gets.</param>
    /// <param name="childEnvironment">The environment of the child process, after the test sets it.</param>
    public static void RequireExplicitEndpoint(string home, IDictionary<string, string?> childEnvironment)
    {
        if (!File.Exists(Path.Combine(home, "netclaw.pid")))
            return;

        var port = ReadEnvironmentPort(childEnvironment) ?? ReadDaemonPort(home);
        if (port is not null && port != RealDaemonPort)
            return;

        throw new InvalidOperationException(
            $"The test home '{home}' has a PID file and no daemon port other than {RealDaemonPort}. A shutdown notice "
            + "from this home would reach port 5199, the port of the real daemon. Start the stand-in with "
            + "StandInDaemon.Start, give the home a port with StandInDaemon.ListenOnly, "
            + "or set NETCLAW_Daemon__Port in the environment of the child process.");
    }

    /// <summary>
    /// Throws unless the child environment sets <c>NETCLAW_Daemon__Port</c> to a port other than
    /// 5199. Use it before a test starts the real <c>netclawd</c>. A daemon that passes its startup
    /// checks by mistake then binds that port, not the port of the real daemon.
    /// </summary>
    public static void RequireDaemonBindPort(IDictionary<string, string?> childEnvironment)
    {
        var port = ReadEnvironmentPort(childEnvironment);
        if (port is null || port == RealDaemonPort)
            throw new InvalidOperationException(
                $"A test must set NETCLAW_Daemon__Port to a port other than {RealDaemonPort} before it starts netclawd. "
                + "The real daemon of the machine binds that port.");
    }

    /// <summary>Finds a free loopback port. Another process can take it before the caller binds it.</summary>
    public static int GetFreePort() => FreePort();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (Process is not null)
        {
            if (!Process.HasExited)
                Process.Kill();
            Process.Dispose();
        }

        _listener.Close();
        _acceptLoop.GetAwaiter().GetResult();

        if (_standInFolder is not null)
            DisposableTempDir.Delete(_standInFolder);
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // the listener closed in Dispose
            }

            // Record before the reply. The caller sees the reply only after the request is on the list.
            var request = context.Request;
            lock (_gate)
                _requests.Add(new RecordedRequest(request.HttpMethod, request.Url!.AbsolutePath, request.Url.Query));

            // The CLI can drop the request at its own timeout on a loaded host. A failed reply must
            // not fault the loop, or Dispose throws and skips the cleanup of the other stand-ins.
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Close();
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException) // slopwatch-ignore: SW003 the client left; the request is already recorded
            {
                continue;
            }
        }
    }

    private static (HttpListener Listener, int Port) BindListener()
    {
        // Another process can take the port between the probe and the bind. Pick a new port.
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException) when (attempt < BindAttempts) // slopwatch-ignore: SW003 port race, retried with a new port
            {
                listener.Close();
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    // This rewrites the whole file with JsonNode.ToJsonString(), so it drops the comments of an
    // existing netclaw.json. No test that uses a stand-in depends on those comments.
    private static void WriteDaemonPort(string home, int port)
    {
        var configDirectory = Path.Combine(home, "config");
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, "netclaw.json");

        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException($"'{path}' is not a JSON object.")
            : new JsonObject { ["configVersion"] = 1 };
        var daemon = root["Daemon"] as JsonObject ?? new JsonObject();
        daemon["Port"] = port;
        root["Daemon"] = daemon;
        File.WriteAllText(path, root.ToJsonString());
    }

    private static int? ReadDaemonPort(string home)
    {
        var path = Path.Combine(home, "config", "netclaw.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return node?["Daemon"]?["Port"]?.GetValue<int>();
        }
        catch (JsonException) // slopwatch-ignore: SW003 a broken config holds no port; the caller then reports the missing port
        {
            return null;
        }
    }

    // The configuration binder ignores key case, so the child process accepts any case here.
    private static int? ReadEnvironmentPort(IDictionary<string, string?> environment)
    {
        foreach (var (key, value) in environment)
        {
            if (string.Equals(key, "NETCLAW_Daemon__Port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                return port;
        }

        return null;
    }
}
