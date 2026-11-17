// -----------------------------------------------------------------------
// <copyright file="DaemonConfigurationSources.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>The configuration sources the daemon and the CLI both read, and how a failure to read them is worded.</summary>
public static class DaemonConfigurationSources
{
    /// <summary>
    /// Adds the daemon configuration sources. A later source overrides an earlier source:
    /// netclaw.json, then secrets.json, then <c>NETCLAW_*</c> environment variables.
    /// </summary>
    public static IConfigurationBuilder AddNetclawDaemonSources(this IConfigurationBuilder builder, NetclawPaths paths)
        => AddOverlays(builder.AddNetclawJsonFile(paths.NetclawConfigPath), paths);

    /// <summary>
    /// Adds captured netclaw.json bytes, then the canonical overlays. A null stream means the optional file is absent.
    /// ConfigurationManager consumes the stream immediately; other builders need it until Build completes.
    /// </summary>
    public static IConfigurationBuilder AddNetclawDaemonSources(
        this IConfigurationBuilder builder, NetclawPaths paths, Stream? netclawJson)
    {
        if (netclawJson is not null)
        {
            try
            {
                builder.AddJsonStream(netclawJson);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new InvalidDataException($"Failed to load configuration from file '{paths.NetclawConfigPath}'.", ex);
            }
        }

        return AddOverlays(builder, paths);
    }

    private static IConfigurationBuilder AddOverlays(IConfigurationBuilder builder, NetclawPaths paths) =>
        builder
            .AddNetclawJsonFile(paths.SecretsPath)
            .AddEnvironmentVariables("NETCLAW_");

    /// <summary>Adds an optional file but rejects a link with a missing target.</summary>
    public static IConfigurationBuilder AddNetclawJsonFile(this IConfigurationBuilder builder, string file)
        => builder.AddJsonFile(file, optional: new FileInfo(file).LinkTarget is null, reloadOnChange: false);

    /// <summary>
    /// One line for a netclaw.json or secrets.json that the JSON provider could not load:
    /// <c>Couldn't read &lt;file&gt;: &lt;reason&gt;</c>. The provider wraps the cause in
    /// <see cref="InvalidDataException"/> and <see cref="FormatException"/>; the innermost message
    /// says what is wrong, with line and position for a parse error.
    /// </summary>
    public static string DescribeLoadFailure(Exception ex)
    {
        var file = "netclaw.json or secrets.json";
        for (var e = ex; e is not null; e = e.InnerException)
        {
            const string prefix = "Failed to load configuration from file '";
            if (e is InvalidDataException && e.Message.StartsWith(prefix, StringComparison.Ordinal)
                && e.Message.IndexOf('\'', prefix.Length, StringComparison.Ordinal) is var end and > 0)
            {
                file = Path.GetFileName(e.Message[prefix.Length..end]);
                break;
            }
        }

        return DescribeLoadFailure(file, ex);
    }

    public static string DescribeLoadFailure(string file, Exception ex)
    {
        while (ex is InvalidDataException or FormatException && ex.InnerException is { } inner)
            ex = inner;

        var reason = ex.Message;
        var lineEnd = reason.IndexOfAny(['\r', '\n']);
        return $"Couldn't read {file}: {(lineEnd < 0 ? reason : reason[..lineEnd])}";
    }
}
