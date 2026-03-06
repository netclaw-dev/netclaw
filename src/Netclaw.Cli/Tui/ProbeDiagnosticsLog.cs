using System.Text;
using System.Diagnostics;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui;

internal static class ProbeDiagnosticsLog
{
    private static readonly object Gate = new();

    public static void Write(
        NetclawPaths paths,
        string source,
        string providerType,
        string? endpoint,
        string probeId,
        string outcome,
        string? detail = null,
        TimeSpan? elapsed = null,
        Exception? exception = null)
    {
        try
        {
            paths.EnsureDirectoriesExist();
            var logPath = Path.Combine(paths.LogsDirectory, "provider-probe.log");

            var endpointHost = GetEndpointHost(endpoint);
            var elapsedMs = elapsed.HasValue ? ((long)elapsed.Value.TotalMilliseconds).ToString() : "-";
            var exceptionType = exception?.GetType().Name ?? "-";
            var exceptionMessage = exception?.Message ?? "-";

            var line = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(" source=").Append(source)
                .Append(" probeId=").Append(probeId)
                .Append(" provider=").Append(providerType)
                .Append(" endpointHost=").Append(endpointHost)
                .Append(" outcome=").Append(outcome)
                .Append(" elapsedMs=").Append(elapsedMs)
                .Append(" detail=").Append(Sanitize(detail ?? "-"))
                .Append(" exType=").Append(exceptionType)
                .Append(" ex=").Append(Sanitize(exceptionMessage))
                .AppendLine()
                .ToString();

            lock (Gate)
            {
                File.AppendAllText(logPath, line);
            }
        }
        catch (Exception ex)
        {
            // Never fail probe flow because diagnostics logging failed.
            Debug.WriteLine($"Probe diagnostics log write failed: {ex.Message}");
        }
    }

    private static string GetEndpointHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "default";

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? (uri.Host.Length > 0 ? uri.Host : "invalid")
            : "invalid";
    }

    private static string Sanitize(string input)
        => input.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
