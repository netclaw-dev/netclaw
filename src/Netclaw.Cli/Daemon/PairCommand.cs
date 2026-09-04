// -----------------------------------------------------------------------
// <copyright file="PairCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Handles the <c>netclaw pair &lt;endpoint&gt;</c> command.
///
/// <para>This is an offline pairing command — it does not require a local daemon.
/// It POSTs a pairing code (generated on the daemon host via <c>netclaw daemon pair</c>)
/// to the remote exchange endpoint, receives a bearer token, and persists both the
/// token and the endpoint to the local config files.</para>
/// </summary>
internal static class PairCommand
{
    private const int MaximumErrorResponseBytes = 4 * 1024;

    /// <summary>
    /// Entry point for <c>netclaw pair [endpoint]</c>.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, NetclawPaths paths)
    {
        using var handler = CreateHttpHandler();
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return await RunAsync(
            args,
            paths,
            httpClient,
            Console.In,
            Console.Out,
            Console.Error,
            CancellationToken.None);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        HttpClient httpClient,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var endpoint = args.Length > 1 ? args[1] : null;

        if (string.IsNullOrWhiteSpace(endpoint) || IsHelpToken(endpoint))
        {
            WritePairHelp(output);
            return string.IsNullOrWhiteSpace(endpoint) ? 1 : 0;
        }

        if (!TryNormalizeEndpoint(endpoint, out var normalizedEndpoint, out var endpointError))
        {
            error.WriteLine($"error: {endpointError}");
            return 1;
        }

        endpoint = normalizedEndpoint;

        var pairingInput = await ReadPairingInputAsync(input, output, error, cancellationToken);
        if (pairingInput is null)
            return 1;

        var exchangeUrl = $"{endpoint}/api/pair/exchange";
        var token = await RequestTokenAsync(
            httpClient,
            exchangeUrl,
            pairingInput.Code,
            pairingInput.DeviceName,
            error,
            cancellationToken);
        if (token is null)
            return 1;

        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        secrets["DeviceToken"] = token;
        ConfigFileHelper.WriteSecretsFile(paths, secrets);

        ClientConfigFile.WriteEndpoint(paths, endpoint);

        output.WriteLine($"Paired successfully as '{pairingInput.DeviceName}'.");
        output.WriteLine($"Token stored in:     {paths.SecretsPath}");
        output.WriteLine($"Endpoint saved in:   {paths.ClientConfigPath}");
        output.WriteLine();
        output.WriteLine($"You can now use `netclaw chat`, `netclaw status`, etc. against {endpoint}.");
        return 0;
    }

    private static async Task<PairingInput?> ReadPairingInputAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        output.Write("Pairing code (XXXX-XXXX): ");
        var code = (await input.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            error.WriteLine("error: pairing code is required.");
            return null;
        }

        var defaultName = Environment.MachineName;
        output.Write($"Device name [{defaultName}]: ");
        var nameInput = (await input.ReadLineAsync(cancellationToken))?.Trim();
        var deviceName = string.IsNullOrWhiteSpace(nameInput) ? defaultName : nameInput;
        return new PairingInput(code, deviceName);
    }

    private static async Task<string?> RequestTokenAsync(
        HttpClient httpClient,
        string exchangeUrl,
        string code,
        string deviceName,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestBody = new { code, deviceName };
            using var response = await httpClient.PostAsJsonAsync(exchangeUrl, requestBody, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await WriteFailureAsync(response, error, cancellationToken);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<ExchangeResponse>(cancellationToken);
            if (string.IsNullOrWhiteSpace(result?.Token))
            {
                error.WriteLine("Pairing failed: the daemon returned an empty token.");
                return null;
            }

            return result.Token;
        }
        catch (HttpRequestException ex)
        {
            error.WriteLine($"Failed to connect to {exchangeUrl}: {ex.Message}");
            error.WriteLine("Make sure that the daemon runs and that the endpoint is available.");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error.WriteLine($"Pairing failed because {exchangeUrl} did not respond before the timeout.");
            return null;
        }
        catch (JsonException)
        {
            error.WriteLine("Pairing failed because the daemon returned invalid JSON.");
            return null;
        }
        catch (NotSupportedException)
        {
            error.WriteLine("Pairing failed because the daemon returned an unsupported response format.");
            return null;
        }
    }

    internal static SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
    };

    private static bool IsHelpToken(string s) => CliArgsParser.IsHelpToken(s);

    private static async Task WriteFailureAsync(
        HttpResponseMessage response,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var daemonError = await ReadDaemonErrorAsync(response.Content, cancellationToken);
        var detail = string.IsNullOrWhiteSpace(daemonError)
            ? response.ReasonPhrase ?? "The daemon rejected the request."
            : daemonError;
        error.WriteLine($"Pairing failed ({(int)response.StatusCode}): {detail}");

        switch (response.StatusCode)
        {
            case HttpStatusCode.Conflict:
                error.WriteLine("Select a different device name and reuse the same unexpired pairing code.");
                break;
            case HttpStatusCode.NotFound:
                error.WriteLine("No active pairing code exists on the daemon.");
                WriteNewCodeHelp(error);
                break;
            case HttpStatusCode.Unauthorized:
                error.WriteLine("The pairing code is invalid, expired, or already used.");
                WriteNewCodeHelp(error);
                break;
            case HttpStatusCode.TooManyRequests:
                WriteRateLimitHelp(response, error);
                break;
            default:
                error.WriteLine("Check the daemon logs, then retry the pairing command.");
                break;
        }
    }

    private static async Task<string?> ReadDaemonErrorAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var body = new byte[MaximumErrorResponseBytes + 1];
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var totalRead = 0;
        while (totalRead < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(totalRead), cancellationToken);
            if (read == 0)
                break;

            totalRead += read;
        }

        if (totalRead == 0 || totalRead > MaximumErrorResponseBytes)
            return null;

        try
        {
            using var document = JsonDocument.Parse(body.AsMemory(0, totalRead));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool TryNormalizeEndpoint(
        string endpoint,
        out string normalizedEndpoint,
        out string error)
    {
        normalizedEndpoint = string.Empty;
        error = string.Empty;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "The daemon endpoint must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "The daemon endpoint must not contain user credentials.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "A non-loopback daemon endpoint must use HTTPS.";
            return false;
        }

        normalizedEndpoint = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    private static void WriteNewCodeHelp(TextWriter error)
        => error.WriteLine("Run `netclaw daemon pair` on the daemon host, then retry with the new code.");

    private static void WriteRateLimitHelp(HttpResponseMessage response, TextWriter error)
    {
        if (response.Headers.RetryAfter?.Delta is { } delay)
        {
            error.WriteLine($"Wait at least {Math.Ceiling(delay.TotalSeconds)} seconds before another attempt.");
            return;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            error.WriteLine($"Wait until {date:R} before another attempt.");
            return;
        }

        error.WriteLine("Wait before another attempt.");
    }

    private static void WritePairHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw pair <endpoint>");
        output.WriteLine();
        output.WriteLine("Pair this device with a remote Netclaw daemon for authenticated remote access.");
        output.WriteLine();
        output.WriteLine("Arguments:");
        output.WriteLine("  <endpoint>   HTTPS daemon URL, or an HTTP loopback URL");
        output.WriteLine();
        output.WriteLine("Steps:");
        output.WriteLine("  1. On the daemon host, run:  netclaw daemon pair");
        output.WriteLine("  2. Note the displayed pairing code");
        output.WriteLine("  3. On this device, run:      netclaw pair <endpoint>");
        output.WriteLine("  4. Enter the pairing code when prompted");
        output.WriteLine("  5. Choose a device name (default: hostname)");
        output.WriteLine();
        output.WriteLine("On success, the device token is stored in secrets.json and the endpoint");
        output.WriteLine("is saved to ~/.netclaw/client/config.json for future CLI connections.");
    }

    private sealed record ExchangeResponse(string Token);

    private sealed record PairingInput(string Code, string DeviceName);
}
