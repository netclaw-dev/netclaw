using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Executes an MCP tool call against a server-specific runtime client.
/// Implemented by daemon infrastructure so tool adapters can route calls
/// using per-session isolation policies.
/// </summary>
public interface IMcpToolInvoker
{
    Task<string> InvokeAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolExecutionContext? context,
        CancellationToken ct = default);
}
