using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

internal sealed class ClientConfigFile
{
    public string? Endpoint { get; init; }

    public static string? ReadEndpoint(NetclawPaths paths)
    {
        if (!File.Exists(paths.ClientConfigPath))
            return null;

        var text = File.ReadAllText(paths.ClientConfigPath);
        var config = JsonSerializer.Deserialize<ClientConfigFile>(text);
        return string.IsNullOrWhiteSpace(config?.Endpoint)
            ? null
            : config.Endpoint.TrimEnd('/');
    }

    public static void WriteEndpoint(NetclawPaths paths, string endpoint)
    {
        var dir = Path.GetDirectoryName(paths.ClientConfigPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllText(
            paths.ClientConfigPath,
            JsonSerializer.Serialize(new ClientConfigFile { Endpoint = endpoint.TrimEnd('/') }, ConfigFileHelper.JsonOptions));
    }
}
