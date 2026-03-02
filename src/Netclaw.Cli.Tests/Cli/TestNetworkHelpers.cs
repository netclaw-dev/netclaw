using System.Net;
using System.Net.Sockets;

namespace Netclaw.Cli.Tests.Cli;

internal static class TestNetworkHelpers
{
    public static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
