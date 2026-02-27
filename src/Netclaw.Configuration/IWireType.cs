namespace Netclaw.Configuration;

/// <summary>
/// Marker interface for types that cross a wire boundary (SignalR, HTTP, etc.).
/// Implementations must remain serialization-safe — no behavior, no circular refs.
/// </summary>
public interface IWireType;
