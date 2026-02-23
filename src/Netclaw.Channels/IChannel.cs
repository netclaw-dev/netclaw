using Microsoft.Extensions.Hosting;

namespace Netclaw.Channels;

/// <summary>
/// Marker interface for input/output channels. Each channel is a hosted service
/// that manages one or more sessions through Akka.Streams pipelines.
/// </summary>
public interface IChannel : IHostedService
{
    string ChannelType { get; }

    string DisplayName { get; }

    ChannelHealth GetHealth();
}

public enum ChannelHealthStatus
{
    Healthy,
    Degraded,
    Disconnected
}

public sealed record ChannelHealth(ChannelHealthStatus Status, string? Detail = null);
