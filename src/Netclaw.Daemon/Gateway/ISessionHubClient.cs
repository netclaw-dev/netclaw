using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Strongly-typed SignalR client interface for session output.
/// Used with <see cref="SessionHub"/> (typed hub) so the server can
/// push <see cref="SessionOutputDto"/> to connected clients.
/// </summary>
public interface ISessionHubClient
{
    Task ReceiveOutput(SessionOutputDto output);
}
