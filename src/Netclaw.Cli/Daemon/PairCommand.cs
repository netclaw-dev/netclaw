// -----------------------------------------------------------------------
// <copyright file="PairCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
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
        var endpoint = args.Length > 1 ? args[1] : null;

        if (string.IsNullOrWhiteSpace(endpoint) || IsHelpToken(endpoint))
        {
            WritePairHelp();
            return string.IsNullOrWhiteSpace(endpoint) ? 1 : 0;
        }

        endpoint = endpoint.TrimEnd('/');

        Console.Write("Pairing code (XXXX-XXXX): ");
        var code = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            Console.Error.WriteLine("error: pairing code is required.");
            return 1;
        }

        var defaultName = Environment.MachineName;
        Console.Write($"Device name [{defaultName}]: ");
        var nameInput = Console.ReadLine()?.Trim();
        var deviceName = string.IsNullOrWhiteSpace(nameInput) ? defaultName : nameInput;

        var exchangeUrl = $"{endpoint}/api/pair/exchange";
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        try
        {
            var requestBody = new { code, deviceName };
            var response = await httpClient.PostAsJsonAsync(exchangeUrl, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"Pairing failed ({(int)response.StatusCode}): {body}");
                Console.Error.WriteLine("Check that the pairing code is correct and hasn't expired.");
                Console.Error.WriteLine("Tip: run `netclaw daemon pair` on the daemon host to generate a new code.");
                return 1;
            }

            var result = await response.Content.ReadFromJsonAsync<ExchangeResponse>();
            if (string.IsNullOrWhiteSpace(result?.Token))
            {
                Console.Error.WriteLine("Pairing failed: daemon returned an empty token.");
                return 1;
            }

            // Persist token to secrets.json (encrypted at rest).
            var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
            secrets["DeviceToken"] = result.Token;
            ConfigFileHelper.WriteSecretsFile(paths, secrets);

            // Persist the local client's preferred daemon endpoint separately from
            // daemon-owned netclaw.json.
            ClientConfigFile.WriteEndpoint(paths, endpoint);

            Console.WriteLine($"Paired successfully as '{deviceName}'.");
            Console.WriteLine($"Token stored in:     {paths.SecretsPath}");
            Console.WriteLine($"Endpoint saved in:   {paths.ClientConfigPath}");
            Console.WriteLine();
            Console.WriteLine($"You can now use `netclaw chat`, `netclaw status`, etc. against {endpoint}.");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Failed to connect to {exchangeUrl}: {ex.Message}");
            Console.Error.WriteLine("Ensure the daemon is running and the endpoint is reachable.");
            return 1;
        }
    }

    private static bool IsHelpToken(string s) => CliArgsParser.IsHelpToken(s);

    private static void WritePairHelp()
    {
        Console.WriteLine("Usage: netclaw pair <endpoint>");
        Console.WriteLine();
        Console.WriteLine("Pair this device with a remote Netclaw daemon for authenticated remote access.");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  <endpoint>   Daemon base URL (e.g. http://my-server:5199)");
        Console.WriteLine();
        Console.WriteLine("Steps:");
        Console.WriteLine("  1. On the daemon host, run:  netclaw daemon pair");
        Console.WriteLine("  2. Note the displayed pairing code");
        Console.WriteLine("  3. On this device, run:      netclaw pair <endpoint>");
        Console.WriteLine("  4. Enter the pairing code when prompted");
        Console.WriteLine("  5. Choose a device name (default: hostname)");
        Console.WriteLine();
        Console.WriteLine("On success, the device token is stored in secrets.json and the endpoint");
        Console.WriteLine("is saved to ~/.netclaw/client/config.json for future CLI connections.");
    }

    private sealed record ExchangeResponse(string Token);
}
