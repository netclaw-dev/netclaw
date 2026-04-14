using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Secrets;

/// <summary>
/// Handles <c>netclaw secrets set &lt;key&gt; &lt;value&gt;</c>.
/// Values are encrypted on write — no plaintext ever touches disk.
/// </summary>
internal static class SecretsCommand
{
    public static int Run(string[] args, NetclawPaths paths, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "set" => RunSet(args, paths, writer),
            _ => RunHelp(writer),
        };
    }

    /// <summary>
    /// <c>netclaw secrets set Slack.BotToken xoxb-...</c>
    /// Accepts a dotted key path (e.g. <c>Providers.ollama.ApiKey</c>),
    /// upserts the value into secrets.json, encrypts all leaves, and
    /// writes with hardened permissions.
    /// </summary>
    private static int RunSet(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (args.Length < 4)
        {
            writer.WriteLine("Usage: netclaw secrets set <key> <value>");
            writer.WriteLine();
            writer.WriteLine("Examples:");
            writer.WriteLine("  netclaw secrets set Slack.BotToken xoxb-...");
            writer.WriteLine("  netclaw secrets set Providers.ollama.ApiKey sk-...");
            writer.WriteLine("  netclaw secrets set Search.BraveApiKey BSAL...");
            return 1;
        }

        var keyPath = args[2];
        var value = args[3];

        // Load existing secrets or start fresh
        JsonObject root;
        if (File.Exists(paths.SecretsPath))
        {
            var existing = File.ReadAllText(paths.SecretsPath);
            root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        // Navigate/create the dotted key path and set the value
        var segments = keyPath.Split('.');
        JsonObject current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (current[seg] is JsonObject child)
            {
                current = child;
            }
            else
            {
                var newObj = new JsonObject();
                current[seg] = newObj;
                current = newObj;
            }
        }

        current[segments[^1]] = value;

        // Write with encryption — the protector encrypts all plaintext leaves
        var protector = SecretsProtection.CreateProtector(paths);
        var json = root.ToJsonString(JsonDefaults.Indented);
        SecretsFileWriter.Write(paths.SecretsPath, json, protector);

        writer.WriteLine($"Set {keyPath} (encrypted).");
        return 0;
    }

    private static int RunHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw secrets <subcommand>");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  set <key> <value>   Store an encrypted secret (e.g. Slack.BotToken xoxb-...)");
        return 0;
    }
}
