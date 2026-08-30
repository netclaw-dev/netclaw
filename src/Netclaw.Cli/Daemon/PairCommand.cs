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
    /// <summary>
    /// Entry point for <c>netclaw pair [endpoint]</c>.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, NetclawPaths paths)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

        endpoint = endpoint.TrimEnd('/');

        output.Write("Pairing code (XXXX-XXXX): ");
        var code = (await input.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            error.WriteLine("error: pairing code is required.");
            return 1;
        }

        var defaultName = Environment.MachineName;
        output.Write($"Device name [{defaultName}]: ");
        var nameInput = (await input.ReadLineAsync(cancellationToken))?.Trim();
        var deviceName = string.IsNullOrWhiteSpace(nameInput) ? defaultName : nameInput;

        var exchangeUrl = $"{endpoint}/api/pair/exchange";

        try
        {
            var requestBody = new { code, deviceName };
            using var response = await httpClient.PostAsJsonAsync(exchangeUrl, requestBody, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await WriteFailureAsync(response, error, cancellationToken);
                return 1;
            }

            var result = await response.Content.ReadFromJsonAsync<ExchangeResponse>(cancellationToken);
            if (string.IsNullOrWhiteSpace(result?.Token))
            {
                error.WriteLine("Pairing failed: the daemon returned an empty token.");
                return 1;
            }

            // Persist token to secrets.json (encrypted at rest).
            var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
            secrets["DeviceToken"] = result.Token;
            ConfigFileHelper.WriteSecretsFile(paths, secrets);

            // Persist the local client's preferred daemon endpoint separately from
            // daemon-owned netclaw.json.
            ClientConfigFile.WriteEndpoint(paths, endpoint);

            output.WriteLine($"Paired successfully as '{deviceName}'.");
            output.WriteLine($"Token stored in:     {paths.SecretsPath}");
            output.WriteLine($"Endpoint saved in:   {paths.ClientConfigPath}");
            output.WriteLine();
            output.WriteLine($"You can now use `netclaw chat`, `netclaw status`, etc. against {endpoint}.");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            error.WriteLine($"Failed to connect to {exchangeUrl}: {ex.Message}");
            error.WriteLine("Make sure that the daemon runs and that the endpoint is available.");
            return 1;
        }
    }

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
        var body = await content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
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
        output.WriteLine("  <endpoint>   Daemon base URL (e.g. http://my-server:5199)");
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
}
