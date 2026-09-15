// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

var paths = new NetclawPaths();
paths.EnsureDirectoriesExist();

if (!File.Exists(paths.SecretsPath))
{
    Console.Error.WriteLine($"Netclaw secrets file was not found: {paths.SecretsPath}");
    return 1;
}

var root = JsonNode.Parse(await File.ReadAllTextAsync(paths.SecretsPath))?.AsObject();
var storedToken = root?["Telegram"]?["BotToken"]?.GetValue<string>();
if (string.IsNullOrWhiteSpace(storedToken))
{
    Console.Error.WriteLine("Telegram.BotToken was not found in Netclaw secrets.");
    return 1;
}

var protector = SecretsProtection.CreateProtector(paths);
var token = ISecretsProtector.IsEncrypted(storedToken)
    ? protector.Unprotect(storedToken)
    : storedToken;

var options = new TelegramChannelOptions
{
    Enabled = true,
    BotToken = new SensitiveString(token),
    AllowDirectMessages = true
};

await using var transport = new TelegramTransport(options, NullLogger<TelegramTransport>.Instance);
transport.MessageReceived += message => transport.SendTextAsync(
    message.ChatId,
    $"Netclaw received: {message.Text}");
using var stopSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopSource.Cancel();
};

try
{
    await transport.StartAsync(stopSource.Token);
    Console.WriteLine("Telegram test bot is active.");
    Console.WriteLine("Send a text message to @netclaw_agent_bot.");
    Console.WriteLine("Press Ctrl+C to stop.");
    await Task.Delay(Timeout.InfiniteTimeSpan, stopSource.Token);
}
catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
{
    Console.WriteLine("Telegram test bot stopped.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Telegram test bot failed: {ex.Message}");
    return 1;
}

return 0;
